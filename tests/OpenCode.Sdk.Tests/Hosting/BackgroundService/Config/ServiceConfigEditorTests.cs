using System.Text.Json;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class ServiceConfigEditorTests
{
    [Test]
    [Arguments("all-members")]
    [Arguments("legacy-on")]
    public async Task SetHostnameAsync_Should_Leave_The_Requested_Change_Unmade_When_Stop_Fails(string name)
    {
        var scenario = new ServiceConfigScenario();
        var initial = scenario.Initial(name)!;
        await scenario.SeedAsync(scenario.Paths.ConfigFile!, initial);
        var inventory = scenario.Inventory();
        var cause = new OpenCodeServerException("scripted stop failure");
        scenario.Stop = (options, token) => Task.FromException(cause);
        var failure = await ChangeHostnameFailureAsync(scenario);

        await Assert.That(failure).IsSameReferenceAs(cause);
        await Assert.That(scenario.Stops.Count).IsEqualTo(1);
        string[] events = name == "legacy-on" ? ["read", "rename", "stop"] : ["read", "stop"];
        await Assert.That(scenario.Events).IsEquivalentTo(events, CollectionOrdering.Matching);
        if (name == "all-members")
        {
            await scenario.AssertUnchangedAsync(scenario.Paths.ConfigFile!, initial, inventory);
        }
        else
        {
            var bytes = await scenario.Adapter.TryReadAllBytesAsync(scenario.Paths.ConfigFile!, CancellationToken.None);
            var repaired = ServiceConfigReader.TryReadDocument(bytes!)!;
            await Assert.That(repaired.RemoteRoute).IsEqualTo("0011223344556677");
            await Assert.That(repaired.Hostname).IsEqualTo("legacy-on");
            using var written = JsonDocument.Parse(bytes);
            await Assert.That(written.RootElement.EnumerateObject().Select(static member => member.Name))
                .IsEquivalentTo(["hostname", "env", "remote"], CollectionOrdering.Matching);
            await Assert.That(scenario.Files.Directory.GetFiles(scenario.Root, "*.tmp", SearchOption.AllDirectories)).IsEmpty();
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SetHostnameAsync_Should_Refuse_The_Document_Or_Donor_Before_Stopping(bool donor)
    {
        var scenario = new ServiceConfigScenario(donor ? "beta" : "local");
        var path = donor ? scenario.Paths.LegacyConfigFile! : scenario.Paths.ConfigFile!;
        var initial = scenario.Initial("repeated-member")!;
        await scenario.SeedAsync(path, initial);
        var inventory = scenario.Inventory();

        var failure = await ChangeHostnameFailureAsync(scenario);

        await Assert.That(failure).IsNotNull();
        await Assert.That(failure.Message).Contains(path);
        await Assert.That(scenario.Stops).IsEmpty();
        await scenario.AssertUnchangedAsync(path, initial, inventory);
    }

    private static async Task<OpenCodeServerException?> ChangeHostnameFailureAsync(ServiceConfigScenario scenario)
    {
        scenario.ClearCalls();
        return await Assert.That(async () => await OpenCodeServiceConfiguration.SetHostnameWithSeamsAsync(
            "requested", scenario.Options, scenario.Editor, CancellationToken.None)).Throws<OpenCodeServerException>();
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    [Arguments(4)]
    [Arguments(5)]
    [Arguments(6)]
    [Arguments(7)]
    [Arguments(8)]
    [Arguments(9)]
    [Arguments(10)]
    [Arguments(11)]
    [Arguments(12)]
    [Arguments(13)]
    public async Task ServiceConfigEditor_Should_Stop_Only_The_Operations_The_Cli_Stops(int index)
    {
        var row = ServiceConfigEditData.Cases[index];
        var scenario = new ServiceConfigScenario();
        scenario.Options.InstalledVersion = "0.0.0-local-42";
        await scenario.SeedAsync(scenario.Paths.ConfigFile!, scenario.Initial("all-members")!);
        scenario.ClearCalls();
        scenario.Stop = (options, token) =>
        {
            scenario.Options.Channel = "changed-after-snapshot";
            scenario.Options.InstalledVersion = "changed-after-snapshot";
            return Task.CompletedTask;
        };

        await ServiceConfigEditData.InvokeAsync(scenario, row.Command, CancellationToken.None);

        await Assert.That(scenario.Stops.Count).IsEqualTo(row.Stops);
        foreach (var stop in scenario.Stops)
        {
            await Assert.That(stop.Channel).IsEqualTo("local");
            await Assert.That(stop.InstalledVersion).IsEqualTo("0.0.0-local-42");
            await Assert.That(stop.Pty).IsNull();
        }
        string[] events = row.Stops == 0 ? ["read", "rename"] : ["read", "stop", "read", "rename"];
        await Assert.That(scenario.Events).IsEquivalentTo(events, CollectionOrdering.Matching);
        string[] calls = row.Stops == 0 ? ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, .. scenario.WriteCalls]
            : ["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, "TryReadAllBytesAsync:" + scenario.Paths.ConfigFile, .. scenario.WriteCalls];
        await Assert.That(scenario.Calls).IsEquivalentTo(calls, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("empty-password")]
    [Arguments("absent-password")]
    [Arguments("stored-password")]
    public async Task GetOrCreatePasswordAsync_Should_Return_And_Store_Then_Reuse_The_Password(string name)
    {
        var scenario = new ServiceConfigScenario();
        if (scenario.Initial(name) is { } initial)
        {
            await scenario.SeedAsync(scenario.Paths.ConfigFile!, initial);
        }
        scenario.ClearCalls();
        var editor = new ServiceConfigEditor(scenario.Store, scenario.StopAsync);

        var password = await editor.GetOrCreatePasswordAsync(scenario.Options, CancellationToken.None);

        await Assert.That(password).IsEqualTo(name == "stored-password"
            ? "stored-password" : "ABEiM0RVZneImaq7zN3u_xAhMkNUZXaHmKm6y9zt_g8");
        var bytes = await scenario.Adapter.TryReadAllBytesAsync(scenario.Paths.ConfigFile!, CancellationToken.None);
        await Assert.That(bytes).IsNotNull();
        await Assert.That(ServiceConfigReader.TryReadDocument(bytes)!.Password).IsEqualTo(password);
        await Assert.That(scenario.Calls.Count(call => call.StartsWith("Rename:", StringComparison.Ordinal)))
            .IsEqualTo(name == "stored-password" ? 0 : 1);
        scenario.ClearCalls();
        await Assert.That(await editor.GetOrCreatePasswordAsync(scenario.Options, CancellationToken.None)).IsEqualTo(password);
        await Assert.That(scenario.Calls).IsEquivalentTo(["TryReadAllBytesAsync:" + scenario.Paths.ConfigFile], CollectionOrdering.Matching);
    }

    [Test]
    public async Task GetOrCreatePasswordAsync_Should_Refuse_Blank_Options_Before_Any_Seam()
    {
        var scenario = new ServiceConfigScenario();
        var failure = await Assert.That(async () => await new ServiceConfigEditor(scenario.Store, scenario.StopAsync)
            .GetOrCreatePasswordAsync(new OpenCodeServiceConfigOptions { Channel = " " }, CancellationToken.None)).Throws<ArgumentException>();
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure.ParamName).IsEqualTo("options");
        await Assert.That(scenario.FileSystem.ReceivedCalls()).IsEmpty();
        await Assert.That(scenario.Environment.ReceivedCalls()).IsEmpty();
    }
}
