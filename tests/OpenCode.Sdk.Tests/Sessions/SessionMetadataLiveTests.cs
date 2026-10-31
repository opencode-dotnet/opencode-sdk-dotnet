using System.Globalization;
using System.Text.Json;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Sessions.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Sessions;

/// <summary>
/// The session metadata update's live proof against the pinned server, on the oracles upstream's
/// own tests hold it to (<c>packages/server/test/session-update.test.ts</c> and
/// <c>packages/core/test/session-create.test.ts</c>): the PATCH answers the declared 204, the
/// metadata it carries replaces the session's rather than merging into it, the replacement is
/// logged as the durable <c>session.metadata.updated</c> event, and a session that does not exist
/// answers the typed not-found error.
/// </summary>
[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionMetadataLiveTests(SimulatedDriveServerFixture server)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

    [Test]
    [Timeout(120_000)]
    public async Task UpdateAsync_Should_Replace_The_Metadata_And_Log_The_Durable_Event(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var created = await client.Sessions.CreateSessionAsync(
            new SessionCreateRequest
            {
                Title = "session-metadata-live",
                Location = new LocationPublicRef { Directory = workspace.Path },
                Metadata = Metadata("{\"source\":\"create\",\"stale\":true}"),
            },
            cancellationToken: cancellationToken);
        await Assert.That(created.Status).IsEqualTo(200);
        var sessionId = created.Session.Id;
        var session = client.Sessions.GetSessionClient(sessionId);
        Exception? primaryFailure = null;

        try
        {
            var response = await session.UpdateAsync(
                new SessionUpdateRequest { Metadata = Metadata("{\"source\":\"patch\"}") },
                cancellationToken: cancellationToken);

            await Assert.That(response.Status).IsEqualTo(204);

            // Replacement, not merge: the key the PATCH left out is gone.
            var reread = await session.GetAsync(cancellationToken: cancellationToken);
            await Assert.That(reread.Session.Metadata).IsNotNull();
            // Dictionary/set membership is the contract; enumeration order is not.
            await Assert.That(reread.Session.Metadata!.Keys).IsEquivalentTo(["source"], CollectionOrdering.Any);
            await Assert.That(reread.Session.Metadata["source"].GetString()).IsEqualTo("patch");

            var replay = await new SessionLogTranscript(client.Experimental, sessionId).ReadToEndAsync(
                new ExperimentalSessionLogRequest { Follow = false },
                cancellationToken);
            var logged = replay.OfType<SessionMetadataUpdated>().Single();
            await Assert.That(logged.Data.SessionId).IsEqualTo(sessionId);
            // Dictionary/set membership is the contract; enumeration order is not.
            await Assert.That(logged.Data.Metadata.Keys).IsEquivalentTo(["source"], CollectionOrdering.Any);
            await Assert.That(logged.Data.Metadata["source"].GetString()).IsEqualTo("patch");
            await Assert.That(logged.Durable.AggregateId).IsEqualTo(sessionId);

            Console.WriteLine(
                "session-metadata-live: patch=" + Number(response.Status) +
                " keys=" + string.Join(',', reread.Session.Metadata.Keys) +
                " seq=" + Number(logged.Durable.Seq));
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
            throw;
        }
        finally
        {
            await CleanupAsync(session, primaryFailure);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task UpdateAsync_Should_Return_The_Typed_404_For_A_Missing_Session(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var sessionId = "ses_sdk_missing_" + Guid.NewGuid().ToString("N");

        var response = await client.Sessions.GetSessionClient(sessionId).UpdateAsync(
            new SessionUpdateRequest { Metadata = Metadata("{\"source\":\"patch\"}") },
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);

        await Assert.That(response.Status).IsEqualTo(404);
        await Assert.That(response.Error).IsTypeOf<SessionNotFoundError>();
        await Assert.That((response.Error as SessionNotFoundError)?.SessionId).IsEqualTo(sessionId);
    }

    private static Optional<IReadOnlyDictionary<string, JsonElement>?> Metadata(string json)
    {
        using var document = JsonDocument.Parse(json);
        return new Optional<IReadOnlyDictionary<string, JsonElement>?>(
            document.RootElement.EnumerateObject()
                .ToDictionary(static member => member.Name, static member => member.Value.Clone(), StringComparer.Ordinal));
    }

    private static async Task CleanupAsync(SessionClient session, Exception? primaryFailure)
    {
        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        try
        {
            _ = await session.RemoveAsync(cancellationToken: cleanup.Token);
        }
        catch (Exception exception) when (primaryFailure is not null)
        {
            Console.WriteLine("session-metadata-live: cleanup suppressed " + exception.GetType().Name);
        }
    }

    private static string Number(long value) => value.ToString(CultureInfo.InvariantCulture);
}
