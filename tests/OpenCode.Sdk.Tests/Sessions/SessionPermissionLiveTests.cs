using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Sessions.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Sessions;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class SessionPermissionLiveTests(SimulatedDriveServerFixture server)
{
    private const string PermissionResource = "resource:one";
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(15);

    [Test]
    [Timeout(180_000)]
    public async Task CreatePermissionAsync_Should_Save_An_Always_Grant_And_Consume_The_Request(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var location = (await client.GetLocationAsync(cancellationToken: cancellationToken)).ResolvedLocation;
        var agentId = SimulationConfigSeed.PermissionProbeAgentId;
        var permissionAction = SimulationConfigSeed.PermissionProbeAction;
        await RequireProbeAgentAsync(client, agentId, permissionAction, cancellationToken);
        var createdSession = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
        {
            Title = "session-permission-live",
            Agent = agentId,
            Location = new LocationPublicRef { Directory = workspace.Path },
        }, cancellationToken: cancellationToken);
        var session = client.Sessions.GetSessionClient(createdSession.Session.Id);
        var cleanupState = new PermissionCleanupState();
        var cleanup = CreateCleanup(session, client, location.Project.Id, cleanupState);
        Exception? primaryFailure = null;

        try
        {
            var createdPermission = await session.CreatePermissionAsync(new SessionPermissionCreateRequest
            {
                Action = permissionAction,
                Resources = [PermissionResource],
                Save = [PermissionResource],
                Agent = agentId,
            }, cancellationToken: cancellationToken);
            cleanupState.PermissionId = createdPermission.Permission.Id;
            await Assert.That(createdPermission.Permission.Effect).IsEqualTo(PermissionEffect.Ask);
            var pending = (await session.GetPermissionAsync(cleanupState.PermissionId,
                cancellationToken: cancellationToken)).Permission;
            var listed = await session.ListRequestsAsync(cancellationToken: cancellationToken);
            var listedMatches = listed.Requests.Where(request => request.Id == cleanupState.PermissionId).ToList();
            await Assert.That(listedMatches).Count().IsEqualTo(1);
            foreach (var observed in new[] { pending, listedMatches[0] })
            {
                await Assert.That(observed.Id).IsEqualTo(cleanupState.PermissionId);
                await Assert.That(observed.SessionId).IsEqualTo(createdSession.Session.Id);
                await Assert.That(observed.Action).IsEqualTo(permissionAction);
                await Assert.That(observed.Resources).IsEquivalentTo([PermissionResource]);
                await Assert.That(observed.Save).IsEquivalentTo([PermissionResource]);
            }

            var replied = await session.ReplyToPermissionAsync(
                cleanupState.PermissionId, new SessionPermissionReplyRequest { Decision = PermissionReply.Always },
                cancellationToken: cancellationToken);
            await Assert.That(replied.Status).IsEqualTo(204);
            var consumed = await session.GetPermissionAsync(cleanupState.PermissionId, OpenCodeRequestOptions.NoThrow, cancellationToken);
            await Assert.That(consumed.Status).IsEqualTo(404);
            await Assert.That(consumed.Error).IsTypeOf<PermissionNotFoundError>();
            await Assert.That((consumed.Error as PermissionNotFoundError)?.RequestId).IsEqualTo(cleanupState.PermissionId);

            var saved = await client.Permissions.ListSavedAsync(
                new PermissionSavedListRequest { ProjectId = location.Project.Id }, cancellationToken: cancellationToken);
            var savedMatches = OwnedSavedPermissions(saved.Saved, location.Project.Id);
            await Assert.That(savedMatches).Count().IsEqualTo(1);
            cleanupState.SavedId = savedMatches[0].Id;
            var removed = await client.Permissions.RemoveSavedAsync(
                cleanupState.SavedId, cancellationToken: cancellationToken);
            await Assert.That(removed.Status).IsEqualTo(204);

            var afterRemoval = await client.Permissions.ListSavedAsync(
                new PermissionSavedListRequest { ProjectId = location.Project.Id }, cancellationToken: cancellationToken);
            await Assert.That(afterRemoval.Saved.Any(info => info.Id == cleanupState.SavedId)).IsFalse();
            await Assert.That(OwnedSavedPermissions(afterRemoval.Saved, location.Project.Id)).IsEmpty();

            WriteEvidence(consumed.Status, removed.Status);
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

    private static async Task RequireProbeAgentAsync(
        OpenCodeClient client, string agentId, string permissionAction, CancellationToken cancellationToken)
    {
        _ = await LiveReadiness.WaitAsync(
            token => client.Agents.ListAgentsAsync(cancellationToken: token),
            result => result.Agents.Any(item => item.Id == agentId),
            "permission probe agent", cancellationToken);
        var agent = (await client.Agents.GetAgentAsync(agentId, cancellationToken: cancellationToken)).Agent;
        await Assert.That(agent.Id).IsEqualTo(agentId);
        await Assert.That(agent.Permissions.Any(rule => (rule.Action, rule.Resource, rule.Effect) == (permissionAction, "*", PermissionEffect.Ask))).IsTrue();
    }

    private static OwnedSessionCleanup CreateCleanup(
        SessionClient session,
        OpenCodeClient client,
        string projectId,
        PermissionCleanupState state)
    {
        var cleanup = new OwnedSessionCleanup(session, CleanupTimeout);
        var pending = new PendingPermissionCleanup(session);
        cleanup.MarkTurnCompleted();
        cleanup.Own(
            "pending permission request",
            token => pending.RejectAsync(state.PermissionId, token));
        cleanup.Own(
            "saved permission discovery",
            async token =>
            {
                var saved = await client.Permissions.ListSavedAsync(
                    new PermissionSavedListRequest { ProjectId = projectId },
                    cancellationToken: token);
                state.SavedId ??= OwnedSavedPermissions(saved.Saved, projectId).SingleOrDefault()?.Id;
            });
        cleanup.Own(
            "saved permission removal",
            async token =>
            {
                if (state.SavedId is not null)
                {
                    _ = await client.Permissions.RemoveSavedAsync(state.SavedId, cancellationToken: token);
                }
            });
        return cleanup;
    }

    private static List<PermissionSavedInfo> OwnedSavedPermissions(
        IEnumerable<PermissionSavedInfo> saved,
        string projectId) =>
        [.. saved.Where(info =>
            info.ProjectId == projectId &&
            info.Action == SimulationConfigSeed.PermissionProbeAction &&
            info.Resource == PermissionResource)];

    private static void WriteEvidence(int consumedStatus, int removedStatus) =>
        Console.WriteLine(
            "session-permission-live: effect=ask pending-consumed-status=" + consumedStatus.ToString(
                System.Globalization.CultureInfo.InvariantCulture) +
            " saved-grant-removed-status=" + removedStatus.ToString(
                System.Globalization.CultureInfo.InvariantCulture));

    private sealed class PermissionCleanupState
    {
        public string? PermissionId { get; set; }

        public string? SavedId { get; set; }
    }
}
