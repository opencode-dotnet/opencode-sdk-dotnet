using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Registration;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Ensure;

public sealed class ServiceConfigEnvironmentReaderTests
{
    [Test]
    [Arguments("service-config-env-state-home.json", "A", "1")]
    [Arguments("service-config-env-state-home-lowercase.json", "xdg_state_home", "/elsewhere/state")]
    [Arguments("service-config-env-state-home-only.json", null, null)]
    public async Task ReadAsync_Should_Drop_XDG_STATE_HOME(string fixture, string? key, string? value)
    {
        var fileSystem = new MockFileSystem();
        var path = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "config.json");
        _ = fileSystem.Initialize().WithFile(path)
            .Which(file => file.HasStringContent(new FixtureLoader().LoadJson("BackgroundService." + fixture)));
        var paths = new ServicePaths("registration.json", path, [], null);

        var result = await new ServiceConfigEnvironmentReader(new TestablyServiceFileSystem(fileSystem))
            .ReadAsync(paths, CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Entries).IsNotNull();
        var expected = new Dictionary<string, string>(StringComparer.Ordinal);
        if (key is not null)
        {
            await Assert.That(value).IsNotNull();
            expected.Add(key, value);
        }
        await Assert.That(result.Entries).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadAsync_Should_Return_An_Absent_Map_For_Invalid_Or_Missing_Config(bool invalid)
    {
        var fileSystem = new MockFileSystem();
        var path = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), "config.json");
        if (invalid)
        {
            _ = fileSystem.Initialize().WithFile(path).Which(file => file.HasStringContent(ServiceConfigData.Malformed));
        }

        var result = await new ServiceConfigEnvironmentReader(new TestablyServiceFileSystem(fileSystem))
            .ReadAsync(new ServicePaths("registration.json", path, [], null), CancellationToken.None);

        await Assert.That(result).IsNotNull();
        await Assert.That(result.Entries).IsNull();
    }
}
