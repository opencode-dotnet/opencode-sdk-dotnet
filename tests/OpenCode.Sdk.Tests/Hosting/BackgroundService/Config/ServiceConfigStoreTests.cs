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
    public async Task ReadAsync_Should_Forward_The_Caller_Token_To_Each_Read()
    {
        var scenario = new ServiceConfigScenario("beta");
        await scenario.SeedAsync(scenario.Paths.LegacyConfigFile!, scenario.Initial("all-members")!);
        using var caller = new CancellationTokenSource();

        var config = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, caller.Token);

        await Assert.That(config.Port).IsEqualTo(5123);
        var calls = scenario.FileSystem.ReceivedCalls().Where(static call => call.GetMethodInfo().Name == "TryReadAllBytesAsync").ToArray();
        await Assert.That(calls.Select(static call => (string)call.GetArguments()[0]!)).IsEquivalentTo(
            [scenario.Paths.ConfigFile, scenario.Paths.LegacyConfigFile], CollectionOrdering.Matching);
        foreach (var call in calls)
        {
            await Assert.That(call.GetArguments()[1]).IsEqualTo(caller.Token);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadAsync_Should_Propagate_The_Read_Cancellation_Instance(bool donor)
    {
        var scenario = new ServiceConfigScenario("beta");
        using var caller = new CancellationTokenSource();
        var failure = new OperationCanceledException(caller.Token);
        var path = donor ? scenario.Paths.LegacyConfigFile! : scenario.Paths.ConfigFile!;
        scenario.FileSystem.TryReadAllBytesAsync(path, Arg.Any<CancellationToken>())
            .Returns(_ => Task.FromException<byte[]?>(failure));
        scenario.ClearCalls();

        var observed = await Assert.That(async () =>
        {
            _ = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, caller.Token);
        }).Throws<OperationCanceledException>();

        await Assert.That(observed).IsSameReferenceAs(failure);
        string[] calls = donor
            ? ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, "Exists:" + scenario.Paths.ConfigFile, "TryReadAllBytesAsync:" + path]
            : ["TryReadAllBytesAsync:" + path];
        await Assert.That(scenario.Calls).IsEquivalentTo(calls, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ReadAsync_Should_Observe_Pre_Cancellation_Before_Using_Any_Seam()
    {
        var scenario = new ServiceConfigScenario();
        using var caller = new CancellationTokenSource();
        await caller.CancelOnWorkerAsync();

        var failure = await Assert.That(async () =>
        {
            _ = await OpenCodeServiceConfiguration.ReadWithSeamsAsync(scenario.Options, scenario.Store, caller.Token);
        }).Throws<OperationCanceledException>();

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(scenario.Environment.ReceivedCalls()).IsEmpty();
        await Assert.That(scenario.FileSystem.ReceivedCalls()).IsEmpty();
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
