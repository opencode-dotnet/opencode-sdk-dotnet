using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Agents;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class AgentsClientLiveTests(SimulatedDriveServerFixture server)
{
    private const string BuildAgentId = "build";

    [Test]
    [Timeout(60_000)]
    public async Task ListAgentsAsync_And_GetAgentAsync_Should_Report_The_Build_Agent(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var listed = await LiveReadiness.WaitAsync(
            token => client.Agents.ListAgentsAsync(cancellationToken: token),
            result => result.Agents.Any(item => item.Id == BuildAgentId),
            "seeded agents", cancellationToken);

        await Assert.That(listed.Status).IsEqualTo(200);
        await Assert.That(listed.IsError).IsFalse();
        await Assert.That(listed.Location.Directory).IsEqualTo(workspace.Path);
        var build = listed.Agents.Single(agent => agent.Id == BuildAgentId);
        await AssertBuildAgentAsync(build);

        var found = await client.Agents.GetAgentAsync(BuildAgentId, cancellationToken: cancellationToken);
        await Assert.That(found.Status).IsEqualTo(200);
        await Assert.That(found.IsError).IsFalse();
        await Assert.That(found.Location.Directory).IsEqualTo(workspace.Path);
        await AssertBuildAgentAsync(found.Agent);

        Console.WriteLine(
            "agents-live: list=" + Number(listed.Status) + " get=" + Number(found.Status) + " id=" + build.Id);
    }

    [Test]
    [Timeout(60_000)]
    public async Task GetAgentAsync_Should_Return_The_Typed_404_For_A_Missing_Agent(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        _ = await LiveReadiness.WaitAsync(
            token => client.Agents.ListAgentsAsync(cancellationToken: token),
            result => result.Agents.Any(item => item.Id == BuildAgentId),
            "seeded agents", cancellationToken);
        var agentId = "sdk-live-missing-agent-" + Guid.NewGuid().ToString("N");

        var response = await client.Agents.GetAgentAsync(
            agentId, requestOptions: OpenCodeRequestOptions.NoThrow, cancellationToken: cancellationToken);

        await Assert.That(response.Status).IsEqualTo(404);
        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Error).IsTypeOf<AgentNotFoundError>();
        var missing = response.Error as AgentNotFoundError;
        await Assert.That(missing?.Tag).IsEqualTo("AgentNotFoundError");
        await Assert.That(missing?.AgentId).IsEqualTo(agentId);
        await Assert.That(missing?.Message).Contains(agentId);
    }

    private static async Task AssertBuildAgentAsync(AgentInfo agent)
    {
        await Assert.That(agent.Id).IsEqualTo(BuildAgentId);
        await Assert.That(agent.Name).IsEqualTo("Build");
        await Assert.That(agent.Mode).IsEqualTo(AgentInfoMode.Primary);
        await Assert.That(agent.Hidden).IsFalse();
        await Assert.That(agent.Permissions).Contains(rule =>
            rule is { Action: "question", Resource: "*" } && rule.Effect == PermissionEffect.Allow);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
