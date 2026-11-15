using System.Text.Json.Nodes;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Registration;

public sealed class ServiceDisabledCheckTests
{
    private readonly MockFileSystem _fileSystem = new();
    private readonly IServiceEnvironment _environment = Substitute.For<IServiceEnvironment>();

    [Test]
    public async Task IsDisabledAsync_Should_Migrate_The_Config_But_Not_The_Registration()
    {
        var selection = Select();
        var paths = Paths(selection);
        await SeedAsync(paths.LegacyConfigFile!, ServiceConfigData.DisabledTrue);
        await SeedAsync(paths.LegacyRegistrationFiles[0], ServiceRegistrationData.DevPrereleaseDotted);

        var disabled = await Check().IsDisabledAsync(selection, CancellationToken.None);

        await Assert.That(disabled).IsTrue();
        await Assert.That(await ReadAsync(paths.ConfigFile!)).IsEqualTo(ServiceConfigData.DisabledTrue);
        await Assert.That(_fileSystem.Directory.GetFiles(_fileSystem.Path.GetDirectoryName(paths.RegistrationFile)!))
            .DoesNotContain(paths.RegistrationFile);
        await Assert.That(await ReadAsync(paths.LegacyRegistrationFiles[0]))
            .IsEqualTo(ServiceRegistrationData.DevPrereleaseDotted);
    }

    [Test]
    [Arguments(ServiceConfigData.DisabledFalse)]
    [Arguments(ServiceConfigData.DisabledAsString)]
    [Arguments(ServiceConfigData.DisabledNull)]
    [Arguments(ServiceConfigData.Malformed)]
    [Arguments(null)]
    public async Task IsDisabledAsync_Should_Not_Disable_For_False_Absent_Or_Undecodable_Config(string? config)
    {
        var selection = Select();
        var paths = Paths(selection);
        if (config is not null)
        {
            await SeedAsync(paths.ConfigFile!, config);
        }

        await Assert.That(await Check().IsDisabledAsync(selection, CancellationToken.None)).IsFalse();
    }

    [Test]
    public async Task IsDisabledAsync_Should_Read_A_Legacy_Remote_Shape()
    {
        var selection = Select();
        var paths = Paths(selection);
        var legacy = new JsonObject { ["remote"] = true, ["disabled"] = true };
        await SeedAsync(paths.ConfigFile!, legacy.ToJsonString());

        await Assert.That(await Check().IsDisabledAsync(selection, CancellationToken.None)).IsTrue();
    }

    [Test]
    public async Task IsDisabledAsync_Should_Not_Read_A_Config_For_A_Direct_Registration_File()
    {
        var fileSystem = Substitute.For<IServiceFileSystem>();
        var selection = ServiceSelection.Snapshot(new OpenCodeServerDiscoverOptions
        {
            RegistrationFilePath = _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "direct.json"),
        });

        await Assert.That(await new ServiceDisabledCheck(_environment, fileSystem)
            .IsDisabledAsync(selection, CancellationToken.None)).IsFalse();
        await Assert.That(fileSystem.ReceivedCalls()).IsEmpty();
        await Assert.That(_environment.ReceivedCalls()).IsEmpty();
    }

    private ServiceDisabledCheck Check() => new(_environment, new TestablyServiceFileSystem(_fileSystem));

    private static ServiceSelection Select() =>
        ServiceSelection.Snapshot(new OpenCodeServerDiscoverOptions { Channel = "dev" });

    private ServicePaths Paths(ServiceSelection selection)
    {
        _environment.GetEnvironmentVariable("XDG_STATE_HOME")
            .Returns(_fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "disabled-state"));
        _environment.GetEnvironmentVariable("XDG_CONFIG_HOME")
            .Returns(_fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "disabled-config"));
        return new ServicePathResolver(_environment).Resolve(selection);
    }

    private async Task SeedAsync(string path, string content)
    {
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(path)!);
        using var stream = _fileSystem.File.Create(path);
        using var writer = new StreamWriter(stream);
        await writer.WriteAsync(content);
    }

    private async Task<string> ReadAsync(string path)
    {
        using var stream = _fileSystem.File.OpenRead(path);
        using var reader = new StreamReader(stream);
        return await reader.ReadToEndAsync();
    }
}
