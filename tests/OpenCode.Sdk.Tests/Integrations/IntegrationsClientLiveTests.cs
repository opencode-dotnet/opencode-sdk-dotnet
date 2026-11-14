using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Integrations;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class IntegrationsClientLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task ListIntegrationsAsync_Should_Include_The_Seeded_Provider_When_Plugins_Are_Activated(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });

        var response = await LiveReadiness.WaitAsync(
            token => client.Integrations.ListIntegrationsAsync(cancellationToken: token),
            result => result.Integrations.Any(item => item.Id == SimulationConfigSeed.ProviderId),
            "seeded integration " + SimulationConfigSeed.ProviderId,
            result => string.Join(", ", result.Integrations.Select(item => item.Id)),
            cancellationToken);

        var seeded = response.Integrations.Where(item => item.Id == SimulationConfigSeed.ProviderId).ToList();
        await Assert.That(seeded.Select(item => item.Id)).Contains(SimulationConfigSeed.ProviderId);
        await Assert.That(seeded.Single().Name).IsEqualTo("Simulated");
        await Assert.That(seeded.Single().Methods).Contains(method => method is IntegrationKeyMethod);
    }
}
