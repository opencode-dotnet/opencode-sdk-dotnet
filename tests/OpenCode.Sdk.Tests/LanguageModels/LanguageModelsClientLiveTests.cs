using System.Globalization;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.LanguageModels;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class LanguageModelsClientLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task ListModelsAsync_And_GetDefaultAsync_Should_Report_The_Selected_Sim_Model(
        CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var client = server.CreateClient(new LocationSelector { Directory = workspace.Path });
        var listed = await LiveReadiness.WaitAsync(
            token => client.LanguageModels.ListModelsAsync(cancellationToken: token),
            result => result.Models.Any(item => item.ProviderId == SimulationConfigSeed.ProviderId && item.ModelId == SimulationConfigSeed.ModelId),
            "seeded models", cancellationToken);

        await Assert.That(listed.Status).IsEqualTo(200);
        await Assert.That(listed.IsError).IsFalse();
        await Assert.That(listed.Location.Directory).IsEqualTo(workspace.Path);
        var model = listed.Models.Single(item =>
            item.ProviderId == SimulationConfigSeed.ProviderId && item.ModelId == SimulationConfigSeed.ModelId);
        await Assert.That(model.Name).IsEqualTo(SimulationConfigSeed.ModelName);

        var selected = await client.LanguageModels.GetDefaultAsync(cancellationToken: cancellationToken);
        await Assert.That(selected.Status).IsEqualTo(200);
        await Assert.That(selected.IsError).IsFalse();
        await Assert.That(selected.Location.Directory).IsEqualTo(workspace.Path);
        await Assert.That(selected.Default).IsNotNull();
        await Assert.That(selected.Default?.ProviderId).IsEqualTo(SimulationConfigSeed.ProviderId);
        await Assert.That(selected.Default?.ModelId).IsEqualTo(SimulationConfigSeed.ModelId);

        Console.WriteLine(
            "models-live: list=" + Number(listed.Status) + " default=" + Number(selected.Status) +
            " model=" + model.ProviderId + "/" + model.ModelId);
    }

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
