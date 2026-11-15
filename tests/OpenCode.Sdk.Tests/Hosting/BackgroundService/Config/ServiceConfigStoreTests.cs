using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class ServiceConfigStoreTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadViewAsync_Should_Not_Read_The_Donor_When_The_Config_Is_Present(bool unreadable)
    {
        var scenario = new ServiceConfigScenario("beta");
        var current = scenario.Initial("unknown-member")!;
        await scenario.SeedAsync(scenario.Paths.ConfigFile!, current);
        await scenario.SeedAsync(scenario.Paths.LegacyConfigFile!, scenario.Initial("all-members")!);
        if (unreadable)
        {
            scenario.FileSystem.TryReadAllBytesAsync(scenario.Paths.ConfigFile!, Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        }

        scenario.ClearCalls();
        var inventory = scenario.Inventory();
        var document = await scenario.Store.ReadViewAsync(ServiceSelection.Snapshot(scenario.Options), CancellationToken.None);

        await Assert.That(document.Port).IsEqualTo(unreadable ? null : 4096);
        string[] calls = unreadable
            ? ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, "Exists:" + scenario.Paths.ConfigFile]
            : ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile];
        await Assert.That(scenario.Calls).IsEquivalentTo(calls, CollectionOrdering.Matching);
        await scenario.AssertUnchangedAsync(scenario.Paths.ConfigFile!, current, inventory);
    }

    [Test]
    [Arguments("legacy-on")]
    [Arguments("legacy-off")]
    public async Task ReadViewAsync_Should_Reject_A_Legacy_Shaped_Donor(string name)
    {
        var scenario = new ServiceConfigScenario("beta");
        var initial = await scenario.SeedDonorAsync(name);
        var inventory = scenario.Inventory();

        var document = await scenario.Store.ReadViewAsync(ServiceSelection.Snapshot(scenario.Options), CancellationToken.None);

        await Assert.That(document.Hostname).IsNull();
        await Assert.That(document.LegacyRemote).IsNull();
        await Assert.That(document.Environment).IsNull();
        await AssertDonorCallsAsync(scenario.Calls, scenario.Paths);
        await scenario.AssertUnchangedAsync(scenario.Paths.LegacyConfigFile!, initial, inventory);
    }

    [Test]
    public async Task ReadViewAsync_Should_Read_A_Current_Donor_Only_When_The_Config_Is_Absent()
    {
        var scenario = new ServiceConfigScenario("beta");
        var initial = await scenario.SeedDonorAsync("all-members");
        var inventory = scenario.Inventory();

        var document = await scenario.Store.ReadViewAsync(ServiceSelection.Snapshot(scenario.Options), CancellationToken.None);

        await Assert.That(document.Port).IsEqualTo(5123);
        await Assert.That(document.Environment!["B"]).IsEqualTo("two");
        await AssertDonorCallsAsync(scenario.Calls, scenario.Paths);
        await scenario.AssertUnchangedAsync(scenario.Paths.LegacyConfigFile!, initial, inventory);
    }

    private static async Task AssertDonorCallsAsync(IReadOnlyList<string> calls, ServicePaths paths) =>
        await Assert.That(calls).IsEquivalentTo([
            "TryReadAllBytesAsync:" + paths.ConfigFile, "Exists:" + paths.ConfigFile,
            "TryReadAllBytesAsync:" + paths.LegacyConfigFile], CollectionOrdering.Matching);
}
