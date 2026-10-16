using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Sessions;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionClientLiveTests(SimulatedDriveServerFixture server)
{
    private const string ModelId = "sim-model";
    private const string ProviderId = "sim";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);
    private static readonly TimeSpan EventWait = TimeSpan.FromSeconds(120);

    [Test]
    [Timeout(180_000)]
    public async Task PostMoveAsync_Should_Apply_The_Move_Before_The_Destination_Read(
        CancellationToken cancellationToken)
    {
        using var source = server.CreateWorkspace();
        using var destination = server.CreateWorkspace();
        using var sourceClient = server.CreateClient(new LocationSelector { Directory = source.Path });
        using var destinationClient = server.CreateClient(new LocationSelector { Directory = destination.Path });
        var created = await sourceClient.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = "session-move-live",
            Location = new LocationPublicRef { Directory = source.Path },
            Model = new ModelRef { Id = ModelId, ProviderId = ProviderId },
        }, cancellationToken: cancellationToken);
        var sessionId = created.Session.Id;
        var sourceSession = sourceClient.Sessions.GetSessionClient(sessionId);
        var cleanup = CreateCleanup(sourceSession, sessionId);
        var reader = new OwnedEventReader(EventWait, CleanupTimeout, cancellationToken);
        var probe = new SessionEventProbe(reader);
        Exception? primaryFailure = null;

        try
        {
            // The subscription is attached before the move is admitted, so the applied event is
            // observed by construction rather than by racing the subscription.
            probe.Start(sourceClient.Events.SubscribeAsync(reader.Token));
            await probe.WaitForConnectedAsync(cancellationToken);

            var admitted = await sourceSession.MoveAsync(
                new SessionMoveRequest { Directory = destination.Path },
                cancellationToken: cancellationToken);
            await Assert.That(admitted.Status).IsEqualTo(204);
            await Assert.That(admitted.IsError).IsFalse();

            using var barrier = SessionEventProbe.Barrier(cancellationToken);
            var moved = await probe.WaitForAsync<SessionMoved>(
                applied => applied.Data.SessionId == sessionId
                    && applied.Data.Location.Directory == destination.Path,
                "session.moved carrying the owned session at the destination", barrier.Token);
            await Assert.That(moved.Data.ProjectId).IsNotEmpty();

            var destinationSession = destinationClient.Sessions.GetSessionClient(sessionId);
            var read = await destinationSession.GetAsync(cancellationToken: cancellationToken);
            await Assert.That(read.Status).IsEqualTo(200);
            await Assert.That(read.Session.Id).IsEqualTo(sessionId);
            await Assert.That(read.Session.Location.Directory).IsEqualTo(destination.Path);
            await Assert.That(read.Session.ProjectId).IsEqualTo(moved.Data.ProjectId);

            Console.WriteLine(
                "session-move-live: admission=" + Number(admitted.Status) +
                " applied-event=session.moved destination-project=" + moved.Data.ProjectId);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            if (!exception.Data.Contains(EventDiagnosticSummary.DataKey))
            {
                exception.Data[EventDiagnosticSummary.DataKey] = probe.DiagnosticSummary;
            }
        }
        finally
        {
            await CompleteCleanupAsync(reader, cleanup, primaryFailure);
        }
    }

    [Test]
    [Timeout(180_000)]
    public async Task PostMoveAsync_Should_Return_The_Typed_404_For_A_Missing_Session(
        CancellationToken cancellationToken)
    {
        using var destination = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = destination.Path });
        var sessionId = "ses_sdk_missing_" + Guid.NewGuid().ToString("N");
        var response = await client.Sessions.GetSessionClient(sessionId).MoveAsync(
            new SessionMoveRequest { Directory = destination.Path },
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);

        await Assert.That(response.Status).IsEqualTo(404);
        await Assert.That(response.Error).IsTypeOf<SessionNotFoundError>();
        var error = response.Error as SessionNotFoundError;
        await Assert.That(error?.Tag).IsEqualTo("SessionNotFoundError");
        await Assert.That(error?.SessionId).IsEqualTo(sessionId);

        Console.WriteLine(
            "session-move-missing-live: status=" + Number(response.Status) + " error=" + error?.Tag);
    }

    private static OwnedSessionCleanup CreateCleanup(SessionClient session, string sessionId)
    {
        var cleanup = new OwnedSessionCleanup(
            _ => Task.CompletedTask,
            token => RemoveOwnedSessionAsync(session, sessionId, token),
            CleanupTimeout);
        cleanup.MarkTurnCompleted();
        return cleanup;
    }

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

    private static async Task CompleteCleanupAsync(
        OwnedEventReader reader,
        OwnedSessionCleanup cleanup,
        Exception? primaryFailure)
    {
        try
        {
            await reader.CompleteAsync(primaryFailure);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }

        await cleanup.CompleteAsync(primaryFailure);
    }

    private static string Number(int value) =>
        value.ToString(CultureInfo.InvariantCulture);
}
