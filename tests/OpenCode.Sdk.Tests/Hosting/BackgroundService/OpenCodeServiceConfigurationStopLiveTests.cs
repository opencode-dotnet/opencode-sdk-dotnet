using System.Security.Cryptography;
using System.Text.Json;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService;

[ClassDataSource<PinnedManagedServiceFixture>]
[NotInParallel]
public sealed class OpenCodeServiceConfigurationStopLiveTests(PinnedManagedServiceFixture service)
{
    private const string Name = "SDK_CONFIG_STOP_WITNESS";
    private const string Value = "persisted-after-stop";
    private static readonly TimeSpan ExitBound = TimeSpan.FromSeconds(30);

    [Test]
    [Timeout(180_000)]
    public async Task SetEnvironmentVariableAsync_Should_Stop_The_Managed_Service_And_Preserve_Its_Config(CancellationToken cancellationToken)
    {
        var scenario = new ServiceConfigLiveScenario(service);
        var environment = scenario.Environment;
        environment.ProcessId.Returns(HostDescriptors.CurrentProcessId());
        var files = scenario.Files;
        var time = TimeProvider.System;
        using var random = RandomNumberGenerator.Create();
        var store = new ServiceConfigStore(environment, files, time, new RandomCredential(random));
        var options = new OpenCodeServiceConfigOptions { Channel = PinnedManagedServiceFixture.Channel };
        var paths = store.ResolvePaths(ServiceSelection.Snapshot(options));
        var before = await files.TryReadAllBytesAsync(paths.ConfigFile!, cancellationToken);
        await Assert.That(before).IsNotNull();
        var editor = new ServiceConfigEditor(store,
            (stopOptions, token) => OpenCodeServer.SystemStopper(environment, files, time).StopAsync(stopOptions, token));

        await OpenCodeServiceConfiguration.SetEnvironmentVariableWithSeamsAsync(Name, Value, options, editor, cancellationToken);

        await Assert.That(await ProcessObservation.ObserveExitWithinAsync(service.ProcessId, ExitBound, cancellationToken)).IsTrue();
        var fileSystem = new RealFileSystem();
        await Assert.That(service.RegistrationFile).Satisfies(path => !fileSystem.File.Exists(path));
        var after = await files.TryReadAllBytesAsync(paths.ConfigFile!, cancellationToken);
        await Assert.That(after).IsNotNull();
        using var original = JsonDocument.Parse(before);
        using var written = JsonDocument.Parse(after);
        await Assert.That(written.RootElement.EnumerateObject().Select(static member => member.Name))
            .IsEquivalentTo([.. original.RootElement.EnumerateObject().Select(static member => member.Name), "env"], CollectionOrdering.Matching);
        foreach (var member in original.RootElement.EnumerateObject())
        {
            await Assert.That(written.RootElement.GetProperty(member.Name).GetRawText()).IsEqualTo(member.Value.GetRawText());
        }
        var entries = written.RootElement.GetProperty("env").EnumerateObject().ToArray();
        await Assert.That(entries.Select(static member => member.Name)).IsEquivalentTo([Name], CollectionOrdering.Matching);
        await Assert.That(entries[0].Value.GetString()).IsEqualTo(Value);
        var text = System.Text.Encoding.UTF8.GetString(after);
        await Assert.That(text).StartsWith("{\n  \"");
        await Assert.That(text).EndsWith("}\n");
    }
}
