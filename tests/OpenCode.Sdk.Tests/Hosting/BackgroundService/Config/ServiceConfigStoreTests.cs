using System.Text;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class ServiceConfigStoreTests
{
    [Test]
    public async Task ReadForWriteAsync_Should_Refuse_Donor_Names_That_Collide_After_Decoding_Before_Copying()
    {
        var scenario = new ServiceConfigScenario("beta");
        var donor = scenario.Paths.LegacyConfigFile!;
        var bytes = ServiceConfigWriteData.CollidingDonorNames;
        await scenario.SeedAsync(donor, bytes);
        scenario.ClearCalls();
        var inventory = scenario.Inventory();

        var failure = await Assert.That(async () =>
            await scenario.Store.ReadForWriteAsync(ServiceSelection.Snapshot(scenario.Options), CancellationToken.None))
            .Throws<OpenCodeServerException>();

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure.Message).Contains(donor);
        await Assert.That(scenario.Inventory()).IsEquivalentTo(inventory, CollectionOrdering.Matching);
        await Assert.That(await scenario.Adapter.TryReadAllBytesAsync(donor, CancellationToken.None))
            .IsEquivalentTo(bytes, CollectionOrdering.Matching);
        await AssertDonorCallsAsync(scenario.Calls, scenario.Paths);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadForWriteAsync_Should_Copy_A_Current_Donor_As_Decoded_Text_And_Return_Its_Password(bool bom)
    {
        var scenario = new ServiceConfigScenario("beta");
        var initial = scenario.Initial("all-members")!;
        var bytes = Encoding.UTF8.GetBytes(initial);
        if (bom)
        {
            bytes = [0xef, 0xbb, 0xbf, .. bytes];
        }
        await scenario.SeedAsync(scenario.Paths.LegacyConfigFile!, bytes);
        scenario.ClearCalls();

        var password = await new ServiceConfigEditor(scenario.Store).GetOrCreatePasswordAsync(scenario.Options, CancellationToken.None);

        await Assert.That(password).IsEqualTo("stored-password");
        await Assert.That(await scenario.Adapter.TryReadAllBytesAsync(scenario.Paths.ConfigFile!, CancellationToken.None))
            .IsEquivalentTo(Encoding.UTF8.GetBytes(initial), CollectionOrdering.Matching);
        await Assert.That(await scenario.Adapter.TryReadAllBytesAsync(scenario.Paths.LegacyConfigFile!, CancellationToken.None))
            .IsEquivalentTo(bytes, CollectionOrdering.Matching);
        await Assert.That(scenario.Calls).IsEquivalentTo(scenario.MigrationCalls, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("repeated-member")]
    [Arguments("surrogate-route")]
    public async Task ReadForWriteAsync_Should_Refuse_A_Q2a_Donor_Before_Copying(string name)
    {
        var scenario = new ServiceConfigScenario("beta");
        var initial = await scenario.SeedDonorAsync(name);
        scenario.ClearCalls();
        var inventory = scenario.Inventory();
        var error = await Assert.That(async () =>
            await new ServiceConfigEditor(scenario.Store).GetOrCreatePasswordAsync(scenario.Options, CancellationToken.None))
            .Throws<OpenCodeServerException>();
        await Assert.That(error).IsNotNull();
        await Assert.That(error.Message).Contains(scenario.Paths.LegacyConfigFile!);
        await scenario.AssertUnchangedAsync(scenario.Paths.LegacyConfigFile!, initial, inventory);
        await AssertDonorCallsAsync(scenario.Calls, scenario.Paths);
    }

    [Test]
    public async Task ReadForWriteAsync_Should_Not_Copy_A_Donor_Over_An_Unreadable_Current_File_Before_A_Password_Write()
    {
        var scenario = new ServiceConfigScenario("beta");
        await scenario.SeedAsync(scenario.Paths.ConfigFile!, scenario.Initial("unknown-member")!);
        var initial = await scenario.SeedDonorAsync("all-members");
        scenario.FileSystem.TryReadAllBytesAsync(scenario.Paths.ConfigFile!, Arg.Any<CancellationToken>()).Returns((byte[]?)null);
        scenario.ClearCalls();
        var password = await new ServiceConfigEditor(scenario.Store).GetOrCreatePasswordAsync(scenario.Options, CancellationToken.None);
        await Assert.That(password).IsEqualTo("ABEiM0RVZneImaq7zN3u_xAhMkNUZXaHmKm6y9zt_g8");
        var bytes = await scenario.Adapter.TryReadAllBytesAsync(scenario.Paths.ConfigFile!, CancellationToken.None);
        var document = ServiceConfigReader.TryReadDocument(bytes!);
        await Assert.That(document).IsNotNull();
        await Assert.That(document.Password).IsEqualTo(password);
        await Assert.That(document.Port).IsNull();
        await Assert.That(await scenario.ReadTextAsync(scenario.Paths.LegacyConfigFile!)).IsEqualTo(initial);
        await Assert.That(scenario.Calls).IsEquivalentTo([
            "TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, "Exists:" + scenario.Paths.ConfigFile,
            .. scenario.WriteCalls], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadForWriteAsync_Should_Not_Read_The_Donor_When_The_Config_Is_Present(bool unreadable)
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
        var document = await scenario.Store.ReadForWriteAsync(ServiceSelection.Snapshot(scenario.Options), CancellationToken.None);

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
    public async Task ReadForWriteAsync_Should_Reject_A_Legacy_Shaped_Donor(string name)
    {
        var scenario = new ServiceConfigScenario("beta");
        var initial = await scenario.SeedDonorAsync(name);
        var inventory = scenario.Inventory();

        var document = await scenario.Store.ReadForWriteAsync(ServiceSelection.Snapshot(scenario.Options), CancellationToken.None);

        await Assert.That(document.Hostname).IsNull();
        await Assert.That(document.LegacyRemote).IsNull();
        await Assert.That(document.Environment).IsNull();
        await AssertDonorCallsAsync(scenario.Calls, scenario.Paths);
        await scenario.AssertUnchangedAsync(scenario.Paths.LegacyConfigFile!, initial, inventory);
    }

    [Test]
    public async Task ReadForWriteAsync_Should_Copy_A_Current_Donor_And_Return_Its_Settings()
    {
        var scenario = new ServiceConfigScenario("beta");
        var initial = await scenario.SeedDonorAsync("all-members");

        var document = await scenario.Store.ReadForWriteAsync(ServiceSelection.Snapshot(scenario.Options), CancellationToken.None);

        await Assert.That(document.Port).IsEqualTo(5123);
        await Assert.That(document.Environment!["B"]).IsEqualTo("two");
        await Assert.That(scenario.Calls).IsEquivalentTo(scenario.MigrationCalls, CollectionOrdering.Matching);
        await Assert.That(await scenario.ReadTextAsync(scenario.Paths.ConfigFile!)).IsEqualTo(initial);
        await Assert.That(await scenario.ReadTextAsync(scenario.Paths.LegacyConfigFile!)).IsEqualTo(initial);
    }

    private static async Task AssertDonorCallsAsync(IReadOnlyList<string> calls, ServicePaths paths) =>
        await Assert.That(calls).IsEquivalentTo([
            "TryReadAllBytesAsync:" + paths.ConfigFile, "Exists:" + paths.ConfigFile,
            "TryReadAllBytesAsync:" + paths.LegacyConfigFile], CollectionOrdering.Matching);
}
