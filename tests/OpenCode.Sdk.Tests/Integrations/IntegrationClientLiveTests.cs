using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;

namespace OpenCode.Sdk.Tests.Integrations;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class IntegrationClientLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task ConnectWithKeyAsync_Should_Store_A_Connection_When_The_Seeded_Key_Method_Is_Used(CancellationToken cancellationToken)
    {
        await WithSeededIntegrationAsync(async (client, integration, cleanup) =>
        {
            var label = "sdk-key-" + Guid.NewGuid().ToString("N");
            cleanup.Own("owned connection credential removal", async token =>
            {
                var owned = await ListOwnedIdsAsync(client, label, token);
                foreach (var id in owned)
                {
                    _ = await client.Credentials.RemoveCredentialAsync(id, cancellationToken: token);
                }
            });

            _ = await integration.ConnectWithKeyAsync(new IntegrationConnectKeyRequest
            {
                Key = "inert-local-connection-sentinel",
                Label = label,
            }, cancellationToken: cancellationToken);

            var createdIds = await ListOwnedIdsAsync(client, label, cancellationToken);
            await Assert.That(createdIds.Count).IsEqualTo(1);
        }, cancellationToken);
    }

    [Test]
    [Timeout(60_000)]
    public async Task GetAsync_Should_Return_The_Seeded_Integration_When_Its_Id_Is_Requested(CancellationToken cancellationToken)
    {
        await WithSeededIntegrationAsync(async (_, integration, _) =>
        {
            var response = await integration.GetAsync(cancellationToken: cancellationToken);

            await Assert.That(response.Integration.Id).IsEqualTo(SimulationConfigSeed.ProviderId);
            await Assert.That(response.Integration.Name).IsEqualTo("Simulated");
            await Assert.That(response.Integration.Methods).Contains(method => method is IntegrationKeyMethod);
        }, cancellationToken);
    }

    private async Task WithSeededIntegrationAsync(Func<OpenCodeClient, IntegrationClient, OwnedCleanup, Task> scenario, CancellationToken token)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(15));
        Exception? failure = null;
        try
        {
            // The list handler awaits plugin activation; Get deliberately does not.
            var catalog = await client.Integrations.ListIntegrationsAsync(cancellationToken: token);
            var seeded = catalog.Integrations.Single(item => item.Id == SimulationConfigSeed.ProviderId);
            await Assert.That(seeded.Methods).Contains(method => method is IntegrationKeyMethod);
            await scenario(client, client.Integrations.GetIntegrationClient(SimulationConfigSeed.ProviderId), cleanup);
        }
        catch (Exception exception)
        {
            failure = exception;
        }

        await cleanup.CompleteAsync(failure);
    }

    private static async Task<IReadOnlyList<string>> ListOwnedIdsAsync(OpenCodeClient client, string label, CancellationToken token)
    {
        var response = await client.Credentials.ListCredentialsAsync(cancellationToken: token);
        return [.. response.Credentials
            .Where(entry => entry.Label == label && entry.IntegrationId == SimulationConfigSeed.ProviderId)
            .Select(entry => entry.Id)];
    }
}
