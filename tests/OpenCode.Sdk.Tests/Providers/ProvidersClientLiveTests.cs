using System.Globalization;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Providers;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class ProvidersClientLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task ListProvidersAsync_And_GetProviderAsync_Should_Report_The_Sim_Provider(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var listed = await LiveReadiness.WaitAsync(
            token => client.Providers.ListProvidersAsync(cancellationToken: token),
            result => result.Providers.Any(item => item.Id == SimulationConfigSeed.ProviderId),
            "seeded providers", cancellationToken);

        await Assert.That(listed.Status).IsEqualTo(200);
        await Assert.That(listed.IsError).IsFalse();
        await Assert.That(listed.Location.Directory).IsEqualTo(workspace.Path);
        var provider = listed.Providers.Single(item => item.Id == SimulationConfigSeed.ProviderId);
        await AssertSimProviderAsync(provider);

        var found = await client.Providers.GetProviderAsync(
            SimulationConfigSeed.ProviderId, cancellationToken: cancellationToken);
        await Assert.That(found.Status).IsEqualTo(200);
        await Assert.That(found.IsError).IsFalse();
        await Assert.That(found.Location.Directory).IsEqualTo(workspace.Path);
        await AssertSimProviderAsync(found.Provider);

        Console.WriteLine(
            "providers-live: list=" + Number(listed.Status) + " get=" + Number(found.Status) +
            " id=" + provider.Id);
    }

    [Test]
    [Timeout(60_000)]
    public async Task GetProviderAsync_Should_Return_The_Typed_404_For_A_Missing_Provider(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        _ = await LiveReadiness.WaitAsync(
            token => client.Providers.ListProvidersAsync(cancellationToken: token),
            result => result.Providers.Any(item => item.Id == SimulationConfigSeed.ProviderId),
            "seeded providers", cancellationToken);
        var providerId = "sdk-live-missing-provider-" + Guid.NewGuid().ToString("N");

        var response = await client.Providers.GetProviderAsync(
            providerId, requestOptions: OpenCodeRequestOptions.NoThrow, cancellationToken: cancellationToken);

        await Assert.That(response.Status).IsEqualTo(404);
        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Error).IsTypeOf<ProviderNotFoundError>();
        var missing = response.Error as ProviderNotFoundError;
        await Assert.That(missing?.Tag).IsEqualTo("ProviderNotFoundError");
        await Assert.That(missing?.ProviderId).IsEqualTo(providerId);
        await Assert.That(missing?.Message).Contains(providerId);
    }

    private static async Task AssertSimProviderAsync(ProviderInfo provider)
    {
        await Assert.That(provider.Id).IsEqualTo(SimulationConfigSeed.ProviderId);
        await Assert.That(provider.Name).IsEqualTo(SimulationConfigSeed.ProviderName);
        await Assert.That(provider.Activation).IsEqualTo(ProviderInfoActivation.Enabled);
        await Assert.That(provider.Package).IsEqualTo(SimulationConfigSeed.ProviderPackage);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
