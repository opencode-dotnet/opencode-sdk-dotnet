using System.Runtime.ExceptionServices;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// Proves the location channels against the real pinned server: ambient client headers resolve
/// each workspace through <c>location.get</c>, while session creation and listing use their own
/// explicit body and query fields.
/// </summary>
[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class LocationTargetingLiveTests(SimulatedDriveServerFixture server)
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(10);

    [Test]
    [Timeout(180_000)]
    public async Task GetLocationAsync_Should_Target_Each_Client_Workspace_And_Its_Own_Session(
        CancellationToken cancellationToken)
    {
        using var workspaceA = server.CreateWorkspace();
        using var workspaceB = server.CreateWorkspace();
        using var clientA = server.CreateClient(new LocationSelector { Directory = workspaceA.Path });
        using var clientB = server.CreateClient(new LocationSelector { Directory = workspaceB.Path });
        SessionInfo? sessionA = null;
        SessionInfo? sessionB = null;
        var ownedSessions = new List<OwnedSession>();
        Exception? primaryFailure = null;

        try
        {
            var resolvedA = (await clientA.GetLocationAsync(cancellationToken: cancellationToken)).ResolvedLocation;
            var resolvedB = (await clientB.GetLocationAsync(cancellationToken: cancellationToken)).ResolvedLocation;

            sessionA = (await clientA.Sessions.CreateSessionAsync(
                new SessionCreateRequest
                {
                    Title = "location-targeting-a",
                    Location = new LocationPublicRef
                    {
                        Directory = resolvedA.Directory,
                    },
                },
                cancellationToken: cancellationToken)).Session;
            ownedSessions.Add(new OwnedSession(clientA, sessionA.Id));
            sessionB = (await clientB.Sessions.CreateSessionAsync(
                new SessionCreateRequest
                {
                    Title = "location-targeting-b",
                    Location = new LocationPublicRef
                    {
                        Directory = resolvedB.Directory,
                    },
                },
                cancellationToken: cancellationToken)).Session;
            ownedSessions.Add(new OwnedSession(clientB, sessionB.Id));

            await Assert.That(resolvedA.Directory).IsEqualTo(workspaceA.Path);
            await Assert.That(resolvedB.Directory).IsEqualTo(workspaceB.Path);
            await Assert.That(resolvedA.Directory).IsNotEqualTo(resolvedB.Directory);
            await Assert.That(sessionA.Location.Directory).IsEqualTo(resolvedA.Directory);
            await Assert.That(sessionB.Location.Directory).IsEqualTo(resolvedB.Directory);
            await Assert.That(sessionA.ProjectId).IsEqualTo(resolvedA.Project.Id);
            await Assert.That(sessionB.ProjectId).IsEqualTo(resolvedB.Project.Id);

            var listedA = await clientA.Sessions.ListSessionsAsync(
                new SessionListRequest { Directory = resolvedA.Directory }, cancellationToken: cancellationToken);
            var listedB = await clientB.Sessions.ListSessionsAsync(
                new SessionListRequest { Directory = resolvedB.Directory }, cancellationToken: cancellationToken);
            var idsA = listedA.Sessions.Select(static session => session.Id).ToArray();
            var idsB = listedB.Sessions.Select(static session => session.Id).ToArray();

            await Assert.That(idsA).Contains(sessionA.Id);
            await Assert.That(idsA).DoesNotContain(sessionB.Id);
            await Assert.That(idsB).Contains(sessionB.Id);
            await Assert.That(idsB).DoesNotContain(sessionA.Id);

            Console.WriteLine(
                "location-live: a=" + resolvedA.Directory +
                " project=" + resolvedA.Project.Id +
                " session=" + sessionA.Id +
                "; b=" + resolvedB.Directory +
                " project=" + resolvedB.Project.Id +
                " session=" + sessionB.Id);
        }
        catch (Exception exception)
        {
            primaryFailure = exception;
        }
        finally
        {
            await CleanupSessionsAsync(ownedSessions, primaryFailure);
        }
    }

    private static async Task CleanupSessionsAsync(
        IReadOnlyList<OwnedSession> sessions,
        Exception? primaryFailure)
    {
        var failures = new List<Exception>();
        foreach (var session in sessions)
        {
            await RemoveAsync(session, failures);
        }

        if (primaryFailure is not null)
        {
            if (failures.Count > 0)
            {
                primaryFailure.Data["LocationTargetingLiveTests.CleanupFailures"] = new AggregateException(failures);
            }

            ExceptionDispatchInfo.Capture(primaryFailure).Throw();
        }

        if (failures.Count is 1)
        {
            ExceptionDispatchInfo.Capture(failures[0]).Throw();
        }

        if (failures.Count > 1)
        {
            throw new AggregateException("Multiple failures occurred while removing the owned sessions.", failures);
        }
    }

    private static async Task RemoveAsync(
        OwnedSession session,
        List<Exception> failures)
    {
        using var cleanup = new CancellationTokenSource(CleanupTimeout);
        try
        {
            _ = await session.Client.Sessions.GetSessionClient(session.Id).RemoveAsync(
                cancellationToken: cleanup.Token);
        }
        catch (Exception exception)
        {
            failures.Add(exception);
        }
    }

    private sealed record OwnedSession(OpenCodeClient Client, string Id);
}
