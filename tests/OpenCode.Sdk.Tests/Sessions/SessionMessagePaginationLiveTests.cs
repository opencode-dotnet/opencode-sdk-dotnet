using System.Globalization;
using System.Text.Json;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Sessions;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionMessagePaginationLiveTests(SimulatedDriveServerFixture server)
{
    private const string Prompt = "task-two pagination prompt";
    private const string Reply = "Task two pagination reply.";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

    [Test]
    [Timeout(180_000)]
    public async Task GetMessageAsync_Should_Return_The_Requested_Message_When_Several_Messages_Exist(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = CreateWorkspaceClient(workspace);
        var sessionId = await CreateOwnedSessionAsync(client, workspace.Path, cancellationToken);
        var cleanup = CreateSessionCleanup(client, sessionId, out var session);
        Exception? primaryFailure = null;

        try
        {
            var turn = new SimulatedSessionTurn(server, client, session, sessionId);
            _ = await turn.CompleteAsync(Prompt, Reply, cancellationToken);
            cleanup.MarkTurnCompleted();
            var messages = await EnumerateMessagesAsync(session, cancellationToken);
            var assistants = OwnedReplies(messages);
            await Assert.That(assistants).Count().IsEqualTo(1);
            await Assert.That(messages.OfType<SessionMessageUser>().Select(message => message.Text)).Contains(Prompt);

            var response = await session.GetMessageAsync(assistants[0].Id, cancellationToken: cancellationToken);

            await Assert.That(response.Message).IsTypeOf<SessionMessageAssistant>();
            var assistant = (SessionMessageAssistant)response.Message;
            await Assert.That(assistant.Id).IsEqualTo(assistants[0].Id);
            await Assert.That(assistant.Content.OfType<SessionMessageAssistantText>().Select(text => text.Text)).Contains(Reply);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    [Test]
    [Timeout(180_000)]
    public async Task ListMessagesAsync_Should_Round_Trip_The_Server_Cursor_And_Match_The_Paginator(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = CreateWorkspaceClient(workspace);
        var sessionId = await CreateOwnedSessionAsync(client, workspace.Path, cancellationToken);
        var session = client.Sessions.GetSessionClient(sessionId);
        var turnCompleted = false;
        Exception? primaryFailure = null;

        try
        {
            var turn = new SimulatedSessionTurn(server, client, session, sessionId);
            _ = await turn.CompleteAsync(Prompt, Reply, cancellationToken);
            turnCompleted = true;

            var manual = new List<ISessionMessageInfo>();
            var request = new SessionMessageListRequest { Limit = 1, Order = ListOrder.Ascending };
            while (true)
            {
                var page = await session.ListMessagesAsync(request, cancellationToken: cancellationToken);
                if (page.Messages.Count == 0)
                {
                    await Assert.That(page.Cursor.Next).IsNull();
                    break;
                }

                await Assert.That(page.Messages).Count().IsEqualTo(1);
                await Assert.That(page.Cursor.Next).IsNotNull();
                manual.Add(page.Messages[0]);
                request = new SessionMessageListRequest { Limit = 1, Cursor = page.Cursor.Next };
            }

            var manualUsers = manual.OfType<SessionMessageUser>().Where(message => message.Text == Prompt).ToList();
            var manualAssistants = OwnedReplies(manual);
            await Assert.That(manualUsers).Count().IsEqualTo(1);
            await Assert.That(manualAssistants).Count().IsEqualTo(1);
            await Assert.That(manual.IndexOf(manualUsers[0])).IsLessThan(manual.IndexOf(manualAssistants[0]));

            var enumerated = await EnumerateMessagesAsync(session, cancellationToken);

            var serializer = new GeneratedJsonSerializer();
            var manualIdentity = new List<(string Type, string Id)>();
            var enumeratedIdentity = new List<(string Type, string Id)>();
            foreach (var (source, target) in new[] { (manual, manualIdentity), (enumerated, enumeratedIdentity) })
            {
                foreach (var message in source)
                {
                    using var document = JsonDocument.Parse(serializer.Serialize(message));
                    target.Add((
                        document.RootElement.GetProperty("type").GetString()!,
                        document.RootElement.GetProperty("id").GetString()!));
                }
            }

            await Assert.That(enumeratedIdentity.SequenceEqual(manualIdentity)).IsTrue();
            var enumeratedUsers = enumerated.OfType<SessionMessageUser>().Where(message => message.Text == Prompt).ToList();
            var enumeratedAssistants = OwnedReplies(enumerated);
            await Assert.That(enumeratedUsers).Count().IsEqualTo(1);
            await Assert.That(enumeratedAssistants).Count().IsEqualTo(1);
            await Assert.That(enumerated.IndexOf(enumeratedUsers[0])).IsLessThan(enumerated.IndexOf(enumeratedAssistants[0]));

            var invalid = await session.ListMessagesAsync(new SessionMessageListRequest { Cursor = "garbage" }, OpenCodeRequestOptions.NoThrow, cancellationToken);
            await Assert.That(invalid.Status).IsEqualTo(400);
            await Assert.That(invalid.Error).IsTypeOf<InvalidCursorError>();
            var error = invalid.Error as InvalidCursorError;
            await Assert.That(error?.Tag).IsEqualTo("InvalidCursorError");
            await Assert.That(error?.Message).IsEqualTo("Invalid cursor");

            Console.WriteLine(
                "message-pagination-live: populated-pages=" + Number(manual.Count) + " terminal-page=empty " +
                "paginator-items=" + Number(enumerated.Count) + " invalid-cursor-status=" + Number(invalid.Status) +
                " invalid-cursor-tag=" + error?.Tag);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            await CompleteCleanupAsync(session, turnCompleted, primaryFailure);
        }
    }

    [Test]
    public async Task CleanupAsync_Should_Give_Removal_A_Fresh_Budget_And_Preserve_All_Failures()
    {
        var primaryFailure = new InvalidOperationException("primary failure");
        var removalFailure = new InvalidOperationException("removal failure");
        var removalTokenWasCancelled = true;

        var scenario = new OperationDeadlineScenario();
        var deadline = scenario.Hold("session interrupt");
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cleanup = new OwnedSessionCleanup(
                token =>
                {
                    var interrupted = WaitForCancellationAsync(token);
                    _ = entered.TrySetResult(true);
                    return interrupted;
                },
                token =>
                {
                    removalTokenWasCancelled = token.IsCancellationRequested;
                    return Task.FromException(removalFailure);
                },
                TimeSpan.FromMilliseconds(50), scenario.Deadline);
        var completing = cleanup.CompleteAsync(primaryFailure);
        try
        {
            await entered.Task;
            await deadline.Entered;
            deadline.Expire();
            deadline.Deliver();
            var thrown = await Assert.That(() => completing).Throws<InvalidOperationException>();

            await Assert.That(thrown).IsSameReferenceAs(primaryFailure);
            await Assert.That(removalTokenWasCancelled).IsFalse();
            var cleanupFailures = (AggregateException)primaryFailure.Data[OwnedSessionCleanup.FailuresKey]!;
            await Assert.That(cleanupFailures.InnerExceptions).Count().IsEqualTo(2);
            await Assert.That(cleanupFailures.InnerExceptions[0]).IsTypeOf<TimeoutException>();
            await Assert.That(cleanupFailures.InnerExceptions[0].InnerException).IsSameReferenceAs(deadline.Failure);
            await Assert.That(cleanupFailures.InnerExceptions[1]).IsSameReferenceAs(removalFailure);
            var lateReport = cleanup.LateFailures;
            await Assert.That(lateReport).IsNotNull();
            await lateReport.WaitForAllAsync(CancellationToken.None);
            await Assert.That(lateReport.Failures.Single().Value).IsAssignableTo<OperationCanceledException>();
        }
        finally
        {
            await scenario.DrainAsync(completing);
            if (cleanup.LateFailures is { } report)
            {
                await report.WaitForAllAsync(CancellationToken.None);
            }
        }
    }

    private OpenCodeClient CreateWorkspaceClient(TestWorkspace workspace) =>
        server.CreateClient(new LocationSelector { Directory = workspace.Path });

    private static OwnedSessionCleanup CreateSessionCleanup(
        OpenCodeClient client,
        string sessionId,
        out SessionClient session)
    {
        session = client.Sessions.GetSessionClient(sessionId);
        return new OwnedSessionCleanup(session, CleanupTimeout);
    }

    private static async Task<string> CreateOwnedSessionAsync(
        OpenCodeClient client,
        string directory,
        CancellationToken cancellationToken)
    {
        var created = await client.Sessions.CreateSessionAsync(
            new SessionCreateRequest
            {
                Title = "message-pagination-live",
                Location = new LocationPublicRef { Directory = directory },
                Model = new ModelRef { Id = "sim-model", ProviderId = "sim" },
            },
            cancellationToken: cancellationToken);
        return created.Session.Id;
    }

    private static async Task CompleteCleanupAsync(
        SessionClient session,
        bool turnCompleted,
        Exception? primaryFailure)
    {
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);
        if (turnCompleted)
        {
            cleanup.MarkTurnCompleted();
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    private static async Task WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = cancellationToken.Register(
            () => _ = cancellation.TrySetCanceled(cancellationToken));
        _ = await cancellation.Task;
    }

    private static async Task<List<ISessionMessageInfo>> EnumerateMessagesAsync(
        SessionClient session,
        CancellationToken cancellationToken)
    {
        var messages = new List<ISessionMessageInfo>();
        await foreach (var message in session.EnumerateMessagesAsync(
                           new SessionMessageListRequest { Limit = 1, Order = ListOrder.Ascending },
                           cancellationToken))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static List<SessionMessageAssistant> OwnedReplies(IEnumerable<ISessionMessageInfo> messages) =>
        [.. messages.OfType<SessionMessageAssistant>()
            .Where(message => message.Content.OfType<SessionMessageAssistantText>().Any(text => text.Text == Reply))];

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
