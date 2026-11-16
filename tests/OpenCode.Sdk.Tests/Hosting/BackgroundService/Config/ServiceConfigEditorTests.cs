using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Config;

public sealed class ServiceConfigEditorTests
{
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
        var editor = new ServiceConfigEditor(scenario.Store);

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
        var failure = await Assert.That(async () => await new ServiceConfigEditor(scenario.Store)
            .GetOrCreatePasswordAsync(new OpenCodeServiceConfigOptions { Channel = " " }, CancellationToken.None)).Throws<ArgumentException>();
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure.ParamName).IsEqualTo("options");
        await Assert.That(scenario.FileSystem.ReceivedCalls()).IsEmpty();
        await Assert.That(scenario.Environment.ReceivedCalls()).IsEmpty();
    }
}
