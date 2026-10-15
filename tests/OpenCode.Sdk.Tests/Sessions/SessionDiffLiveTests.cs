using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Sessions;

/// <summary>
/// The turn-diff route's live proof against the pinned server, and with it the idle turn marker the
/// route is defined by: a fresh session diffs to nothing, a completed simulated turn ends in an idle
/// message on the session's history, the diff anchored on that turn's prompt answers the declared 200
/// with no changed files (the scripted reply writes none), and the two declared refusals - an anchor
/// that is not a user message and an unknown message - answer 400 and 404 on the NoThrow spine.
/// </summary>
[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionDiffLiveTests(SimulatedDriveServerFixture server)
{
    private const string Prompt = "session-diff live prompt";
    private const string Reply = "Session diff live reply.";
    private const string UnknownMessageId = "msg_00000000000000000000000000";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

    [Test]
    [Timeout(180_000)]
    public async Task GetDiffAsync_Should_Answer_The_Declared_Arms_Around_A_Completed_Turn(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var created = await client.Sessions.CreateSessionAsync(
            new SessionCreateRequest
            {
                Title = "session-diff-live",
                Location = new LocationPublicRef { Directory = workspace.Path },
                Model = new ModelRef { Id = "sim-model", ProviderId = "sim" },
            },
            cancellationToken: cancellationToken);
        await Assert.That(created.Status).IsEqualTo(200);
        var session = client.Sessions.GetSessionClient(created.Session.Id);
        var turnCompleted = false;
        Exception? primaryFailure = null;

        try
        {
            var fresh = await session.GetDiffAsync(cancellationToken: cancellationToken);
            await Assert.That(fresh.Status).IsEqualTo(200);
            await Assert.That(fresh.IsError).IsFalse();
            await Assert.That(fresh.Diffs).IsEmpty();

            var turn = new SimulatedSessionTurn(server, client, session, created.Session.Id);
            _ = await turn.CompleteAsync(Prompt, Reply, cancellationToken);
            turnCompleted = true;

            var messages = await EnumerateMessagesAsync(session, cancellationToken);
            var user = messages.OfType<SessionMessageUser>().Single(message => message.Text == Prompt);
            var assistant = messages.OfType<SessionMessageAssistant>().Single(message =>
                message.Content.OfType<SessionMessageAssistantText>().Any(text => text.Text == Reply));
            var idle = messages.OfType<SessionMessageIdle>().Single();
            await Assert.That(messages.IndexOf(idle)).IsGreaterThan(messages.IndexOf(assistant));
            await Assert.That(idle.Outcome).IsEqualTo(SessionMessageIdleOutcome.Succeeded);

            var turnDiff = await session.GetDiffAsync(
                new SessionDiffRequest { From = user.Id, Context = 3 },
                cancellationToken: cancellationToken);
            await Assert.That(turnDiff.Status).IsEqualTo(200);
            await Assert.That(turnDiff.IsError).IsFalse();
            await Assert.That(turnDiff.Diffs).IsEmpty();

            var notUser = await session.GetDiffAsync(
                new SessionDiffRequest { From = assistant.Id },
                OpenCodeRequestOptions.NoThrow,
                cancellationToken);
            await Assert.That(notUser.Status).IsEqualTo(400);
            await Assert.That(notUser.Error).IsTypeOf<InvalidRequestError>();

            var unknown = await session.GetDiffAsync(
                new SessionDiffRequest { From = UnknownMessageId },
                OpenCodeRequestOptions.NoThrow,
                cancellationToken);
            await Assert.That(unknown.Status).IsEqualTo(404);
            await Assert.That(unknown.Error).IsTypeOf<MessageNotFoundError>();
            var missing = (MessageNotFoundError)unknown.Error!;
            await Assert.That(missing.MessageId).IsEqualTo(UnknownMessageId);
            await Assert.That(missing.SessionId).IsEqualTo(created.Session.Id);

            Console.WriteLine(
                "session-diff-live: fresh=" + Number(fresh.Status) +
                " turn=" + Number(turnDiff.Status) + "/" + Number(turnDiff.Diffs.Count) +
                " not-user=" + Number(notUser.Status) +
                " unknown=" + Number(unknown.Status) +
                " idle=" + idle.Outcome);
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

    private static async Task<List<ISessionMessageInfo>> EnumerateMessagesAsync(
        SessionClient session,
        CancellationToken cancellationToken)
    {
        var messages = new List<ISessionMessageInfo>();
        await foreach (var message in session.EnumerateMessagesAsync(
                           new SessionMessageListRequest { Order = ListOrder.Ascending },
                           cancellationToken))
        {
            messages.Add(message);
        }

        return messages;
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
