using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Sessions;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionsClientLiveTests(SimulatedDriveServerFixture server)
{
    private const string ModelId = "sim-model";
    private const string ProviderId = "sim";
    private const string TransferPrompt = "task-two raw transfer prompt";
    private const string TransferReply = "Task two raw transfer reply.";
    private const string StatsPrompt = "task-two statistics prompt";
    private const string StatsReply = "Task two statistics reply.";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private readonly SessionMessageEvidenceProjector _messageEvidence = new();

    [Test]
    [Timeout(180_000)]
    public async Task ForkAsync_Should_Copy_Only_Earlier_History_When_A_Before_Boundary_Is_Supplied(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var created = await CreateOwnedSessionAsync(client, workspace.Path, "session-fork-live", cancellationToken);
        var session = client.Sessions.GetSessionClient(created.Id);
        var cleanup = CreateCleanup(session, created.Id);
        const string firstPrompt = "Fork retained prompt";
        const string firstReply = "Fork retained reply.";
        const string secondPrompt = "Fork excluded prompt";
        const string secondReply = "Fork excluded reply.";
        Exception? primaryFailure = null;

        try
        {
            var turn = new SimulatedSessionTurn(server, client, session, created.Id);
            _ = await turn.CompleteAsync(firstPrompt, firstReply, cancellationToken);
            _ = await client.Experimental.WaitForSessionAsync(created.Id, cancellationToken: cancellationToken);
            _ = await turn.CompleteAsync(secondPrompt, secondReply, cancellationToken);
            _ = await client.Experimental.WaitForSessionAsync(created.Id, cancellationToken: cancellationToken);
            cleanup.MarkTurnCompleted();
            var source = await session.ListMessagesAsync(
                new SessionMessageListRequest { Order = ListOrder.Ascending }, cancellationToken: cancellationToken);
            var boundary = source.Messages.OfType<SessionMessageUser>().Single(message => message.Text == secondPrompt).Id;

            var response = await session.ForkAsync(new SessionForkRequest { Before = boundary }, cancellationToken: cancellationToken);
            var fork = client.Sessions.GetSessionClient(response.Fork.Id);
            cleanup.Own("fork removal", token => RemoveOwnedSessionAsync(fork, response.Fork.Id, token));
            var copied = await fork.ListMessagesAsync(
                new SessionMessageListRequest { Order = ListOrder.Ascending }, cancellationToken: cancellationToken);
            var evidence = _messageEvidence.Project(copied.Messages);

            await Assert.That(evidence.Select(message => message.Text)).DoesNotContain(secondPrompt);
            await Assert.That(evidence.Select(message => message.Text)).DoesNotContain(secondReply);
            await Assert.That(evidence.Where(message => message.Type is "user" or "assistant").Select(message => message.Text))
                .IsEquivalentTo([firstPrompt, firstReply], CollectionOrdering.Matching);
            await Assert.That(response.Fork.Id).IsNotEqualTo(created.Id);
            await Assert.That(response.Fork.Fork).IsNotNull();
            await Assert.That(response.Fork.Fork!.SessionId).IsEqualTo(created.Id);
            await Assert.That(response.Fork.Fork.Boundary).IsTypeOf<SessionForkBoundaryBefore>();
            await Assert.That(((SessionForkBoundaryBefore)response.Fork.Fork.Boundary).MessageId).IsEqualTo(boundary);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    [Test]
    [Timeout(180_000)]
    public async Task PostImportAsync_Should_Preserve_The_Raw_Exported_Session(
        CancellationToken cancellationToken)
    {
        using var source = server.CreateWorkspace();
        using var destination = server.CreateWorkspace();
        using var sourceClient = server.CreateClient(new LocationSelector { Directory = source.Path });
        using var destinationClient = server.CreateClient(new LocationSelector { Directory = destination.Path });
        var created = await CreateOwnedSessionAsync(
            sourceClient, source.Path, "session-transfer-live", cancellationToken);
        var session = sourceClient.Sessions.GetSessionClient(created.Id);
        var cleanup = CreateCleanup(session, created.Id);
        Exception? primaryFailure = null;

        try
        {
            var turn = new SimulatedSessionTurn(server, sourceClient, session, created.Id);
            _ = await turn.CompleteAsync(TransferPrompt, TransferReply, cancellationToken);
            cleanup.MarkTurnCompleted();
            var exported = await sourceClient.Experimental.GetSessionExportAsync(created.Id,
                new ExperimentalSessionExportRequest { Sanitize = false },
                cancellationToken: cancellationToken);
            var exportedEvidence = await AssertRawExportAsync(exported, created.Id);
            var importRequest = CreateImportRequest(exported.SessionExport, destination.Path);

            await AssertDuplicateImportConflictAsync(
                destinationClient, importRequest, created.Id, cancellationToken);

            var removed = await session.RemoveAsync(cancellationToken: cancellationToken);
            await Assert.That(removed.Status).IsEqualTo(204);
            await Assert.That(removed.IsError).IsFalse();

            var imported = await destinationClient.Experimental.ImportSessionAsync(
                importRequest, cancellationToken: cancellationToken);
            await Assert.That(imported.Status).IsEqualTo(200);
            await Assert.That(imported.IsError).IsFalse();
            await Assert.That(imported.SessionImport.Id).IsEqualTo(created.Id);
            await Assert.That(imported.SessionImport.Location.Directory).IsEqualTo(destination.Path);

            var importedMessages = await destinationClient.Sessions.GetSessionClient(created.Id).ListMessagesAsync(
                new SessionMessageListRequest { Order = ListOrder.Ascending },
                cancellationToken: cancellationToken);
            await Assert.That(importedMessages.Status).IsEqualTo(200);
            await Assert.That(importedMessages.IsError).IsFalse();
            var importedEvidence = _messageEvidence.Project(importedMessages.Messages);
            await Assert.That(importedEvidence.SequenceEqual(exportedEvidence)).IsTrue();

            Console.WriteLine(
                "session-transfer-live: export=" + Number(exported.Status) +
                " duplicate=409 import=" + Number(imported.Status) +
                " messages=" + Number(importedEvidence.Count) + " sanitize=false");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            await cleanup.CompleteAsync(primaryFailure);
        }
    }

    [Test]
    [Timeout(180_000)]
    public async Task GetStatsAsync_Should_Report_Positive_Owned_Session_Activity(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var created = await CreateOwnedSessionAsync(client, workspace.Path, "session-stats-live", cancellationToken);
        var session = client.Sessions.GetSessionClient(created.Id);
        var cleanup = CreateCleanup(session, created.Id);
        Exception? primaryFailure = null;

        try
        {
            var turn = new SimulatedSessionTurn(server, client, session, created.Id);
            _ = await turn.CompleteAsync(StatsPrompt, StatsReply, cancellationToken);
            cleanup.MarkTurnCompleted();
            var messages = await session.ListMessagesAsync(
                new SessionMessageListRequest { Order = ListOrder.Ascending },
                cancellationToken: cancellationToken);
            var observed = _messageEvidence.Project(messages.Messages);
            await Assert.That(observed.Count).IsGreaterThan(0);
            var from = (long)Math.Floor(observed.Min(message => message.Created)) - 1;
            var to = (long)Math.Ceiling(observed.Max(message => message.Created)) + 1;

            var response = await client.Experimental.GetSessionStatsAsync(new ExperimentalSessionStatsRequest
            {
                From = from,
                To = to,
                Project = created.ProjectId,
            }, cancellationToken: cancellationToken);

            await Assert.That(response.Status).IsEqualTo(200);
            await Assert.That(response.IsError).IsFalse();
            await Assert.That(response.SessionStats.Range.From).IsEqualTo(from);
            await Assert.That(response.SessionStats.Range.To).IsEqualTo(to);
            await Assert.That(response.SessionStats.Sessions).IsGreaterThan(0);
            await Assert.That(response.SessionStats.Prompts).IsGreaterThan(0);
            await Assert.That(response.SessionStats.Steps).IsGreaterThan(0);
            await Assert.That(response.SessionStats.Models.Any(model =>
                model.Model is { Id: ModelId, ProviderId: ProviderId })).IsTrue();

            Console.WriteLine(
                "session-stats-live: sessions=" + Number(response.SessionStats.Sessions) +
                " prompts=" + Number(response.SessionStats.Prompts) +
                " steps=" + Number(response.SessionStats.Steps) + " model=sim/sim-model");
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            await cleanup.CompleteAsync(primaryFailure);
        }
    }

    [Test]
    [Timeout(180_000)]
    public async Task EnumerateSessionsAsync_Should_Cross_A_Page_Boundary_Carrying_Its_Filter(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var first = await CreateOwnedSessionAsync(client, workspace.Path, "session-pagination-live-1", cancellationToken);
        var second = await CreateOwnedSessionAsync(client, workspace.Path, "session-pagination-live-2", cancellationToken);
        var firstSession = client.Sessions.GetSessionClient(first.Id);
        var secondSession = client.Sessions.GetSessionClient(second.Id);
        var cleanup = CreateCleanup(firstSession, first.Id);
        cleanup.Own("second session removal", token => RemoveOwnedSessionAsync(secondSession, second.Id, token));
        Exception? primaryFailure = null;

        try
        {
            // A one-item page forces continuations, and the project filter must ride every one of
            // them: dropping it would surface sessions from the other tests sharing this server.
            var request = new SessionListRequest
            {
                Limit = 1,
                Order = ListOrder.Ascending,
                Project = first.ProjectId,
            };
            var pages = new List<SessionListResponse>();
            await foreach (var page in client.Sessions
                               .EnumerateSessionsAsync(request, cancellationToken: cancellationToken)
                               .Pages
                               .WithCancellation(cancellationToken))
            {
                pages.Add(page);
            }

            var enumerated = new List<SessionInfo>();
            await foreach (var session in client.Sessions.EnumerateSessionsAsync(request, cancellationToken: cancellationToken))
            {
                enumerated.Add(session);
            }

            await Assert.That(pages.Count).IsGreaterThan(1);
            await Assert.That(pages.All(static page => page is { Status: 200, IsError: false })).IsTrue();
            await Assert.That(pages[^1].Cursor.Next).IsNull();
            await Assert
                .That(pages
                    .SelectMany(static page => page.Sessions)
                    .Select(static session => session.Id)
                    .SequenceEqual(enumerated.Select(static session => session.Id), StringComparer.Ordinal))
                .IsTrue();
            await Assert.That(enumerated.All(session => session.ProjectId == first.ProjectId)).IsTrue();
            await Assert.That(enumerated.Any(session => session.Id == first.Id)).IsTrue();
            await Assert.That(enumerated.Any(session => session.Id == second.Id)).IsTrue();

            Console.WriteLine(
                "session-pagination-live: pages=" + Number(pages.Count) +
                " sessions=" + Number(enumerated.Count) +
                " project=" + first.ProjectId);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            await cleanup.CompleteAsync(primaryFailure);
        }
    }

    private static async Task<SessionInfo> CreateOwnedSessionAsync(
        OpenCodeClient client,
        string directory,
        string title,
        CancellationToken cancellationToken)
    {
        var response = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = title,
            Location = new LocationPublicRef { Directory = directory },
            Model = new ModelRef { Id = ModelId, ProviderId = ProviderId },
        }, cancellationToken: cancellationToken);
        return response.Session;
    }

    private static OwnedSessionCleanup CreateCleanup(SessionClient session, string sessionId)
    {
        var cleanup = new OwnedSessionCleanup(
            async token => _ = await session.InterruptAsync(cancellationToken: token),
            token => RemoveOwnedSessionAsync(session, sessionId, token),
            CleanupTimeout);
        cleanup.MarkTurnStarted();
        return cleanup;
    }

    private async Task<IReadOnlyList<SessionMessageEvidence>> AssertRawExportAsync(
        ExperimentalSessionExportResponse response,
        string sessionId)
    {
        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.SessionExport.Info.Id).IsEqualTo(sessionId);
        await Assert.That(response.SessionExport.Messages.Count).IsGreaterThan(0);
        await Assert.That(response.SessionExport.Messages.OfType<SessionMessageUser>().Any()).IsTrue();
        await Assert.That(response.SessionExport.Messages.OfType<SessionMessageAssistant>().Any()).IsTrue();
        var evidence = _messageEvidence.Project(response.SessionExport.Messages);
        await Assert.That(evidence.Any(message =>
            message is { Type: "user", Text: TransferPrompt })).IsTrue();
        await Assert.That(evidence.Any(message =>
            message is { Type: "assistant", Text: TransferReply })).IsTrue();
        return evidence;
    }

    private static async Task AssertDuplicateImportConflictAsync(
        OpenCodeClient destinationClient,
        ExperimentalSessionImportRequest request,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var response = await destinationClient.Experimental.ImportSessionAsync(
            request, OpenCodeRequestOptions.NoThrow, cancellationToken);

        await Assert.That(response.Status).IsEqualTo(409);
        await Assert.That(response.Error).IsTypeOf<ConflictError>();
        var conflict = response.Error as ConflictError;
        await Assert.That(conflict?.Tag).IsEqualTo("ConflictError");
        await Assert.That(conflict?.Resource).IsEqualTo(sessionId);
    }

    private static ExperimentalSessionImportRequest CreateImportRequest(
        SessionTransferData exported,
        string destination) =>
        new()
        {
            Info = exported.Info,
            Messages = exported.Messages,
            Location = new LocationPublicRef { Directory = destination },
        };

    private static async Task RemoveOwnedSessionAsync(
        SessionClient session,
        string sessionId,
        CancellationToken cancellationToken)
    {
        var response = await session.RemoveAsync(OpenCodeRequestOptions.NoThrow, cancellationToken);
        if (response.Status == 204
            || (response is { Status: 404, Error: SessionNotFoundError missing }
                && missing.SessionId == sessionId))
        {
            return;
        }

        throw new InvalidOperationException(
            "Owned session cleanup returned status " + Number(response.Status) +
            ", error " + response.Error?.GetType().Name + ", body " + response.RawBody + ".");
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
