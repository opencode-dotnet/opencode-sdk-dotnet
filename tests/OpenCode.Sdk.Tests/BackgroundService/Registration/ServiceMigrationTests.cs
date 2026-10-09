using System.Text;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.BackgroundService.Registration;

/// <summary>
/// The pinned CLI's legacy copy (<c>migrateRegistration</c> / <c>migrateConfig</c>) over the
/// repository's filesystem double: the version gate, the donor order, exclusive creation, and the
/// rule that no write failure and no missing directory ever reaches the caller.
/// </summary>
public sealed class ServiceMigrationTests
{
    private const string Channel = "dev";
    private const string CustomChannel = "preview/a";

    private readonly MockFileSystem _fileSystem = new();

    [Test]
    [Arguments(ServiceRegistrationData.DevPrerelease)]
    [Arguments(ServiceRegistrationData.DevPrereleaseDotted)]
    public async Task ApplyAsync_Should_Copy_A_Legacy_Registration_Whose_Version_Belongs_To_The_Channel(string donor)
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyRegistrationFiles[0], donor);

        await Migration().ApplyAsync(Select(Channel), paths, CancellationToken.None);

        await Assert.That(_fileSystem.File.Exists(paths.RegistrationFile)).IsTrue();
        await Assert.That(_fileSystem.File.ReadAllText(paths.RegistrationFile)).IsEqualTo(donor);
        await Assert.That(_fileSystem.File.Exists(paths.LegacyRegistrationFiles[0])).IsTrue();
    }

    [Test]
    [Arguments(ServiceRegistrationData.DevPrereleaseThreeSegments)]
    [Arguments(ServiceRegistrationData.DevPrereleaseWrongChannel)]
    [Arguments(ServiceRegistrationData.StableRelease)]
    [Arguments(ServiceRegistrationData.Versionless)]
    [Arguments(ServiceRegistrationData.Malformed)]
    public async Task ApplyAsync_Should_Not_Copy_A_Legacy_Registration_The_Prefix_Arm_Rejects(string donor)
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyRegistrationFiles[0], donor);

        await Migration().ApplyAsync(Select(Channel), paths, CancellationToken.None);

        await Assert.That(_fileSystem.File.Exists(paths.RegistrationFile)).IsFalse();
    }

    [Test]
    public async Task ApplyAsync_Should_Copy_A_Stable_Registration_Only_Under_An_Exact_Installed_Version()
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyRegistrationFiles[0], ServiceRegistrationData.StableRelease);

        await Migration().ApplyAsync(Select(Channel, installedVersion: "1.2.3"), paths, CancellationToken.None);

        await Assert.That(_fileSystem.File.ReadAllText(paths.RegistrationFile)).IsEqualTo(ServiceRegistrationData.StableRelease);
    }

    [Test]
    public async Task ApplyAsync_Should_Not_Let_A_Different_Installed_Version_Claim_A_Stable_Registration()
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyRegistrationFiles[0], ServiceRegistrationData.StableRelease);

        await Migration().ApplyAsync(Select(Channel, installedVersion: "1.2.4"), paths, CancellationToken.None);

        await Assert.That(_fileSystem.File.Exists(paths.RegistrationFile)).IsFalse();
    }

    [Test]
    public async Task ApplyAsync_Should_Prefer_The_Hashed_Donor_Over_The_Shared_File_For_A_Custom_Channel()
    {
        var paths = ChannelPaths(CustomChannel);
        const string hashed = ServiceRegistrationData.CustomChannelHashedDonor;
        const string shared = ServiceRegistrationData.CustomChannelSharedDonor;
        Seed(paths.LegacyRegistrationFiles[0], hashed);
        Seed(paths.LegacyRegistrationFiles[1], shared);

        await Migration().ApplyAsync(Select(CustomChannel), paths, CancellationToken.None);

        await Assert.That(_fileSystem.File.ReadAllText(paths.RegistrationFile)).IsEqualTo(hashed);
    }

    [Test]
    public async Task ApplyAsync_Should_Fall_Through_To_The_Shared_File_When_The_Hashed_Donor_Is_Absent()
    {
        var paths = ChannelPaths(CustomChannel);
        const string shared = ServiceRegistrationData.CustomChannelSharedDonor;
        Seed(paths.LegacyRegistrationFiles[1], shared);

        await Migration().ApplyAsync(Select(CustomChannel), paths, CancellationToken.None);

        await Assert.That(_fileSystem.File.ReadAllText(paths.RegistrationFile)).IsEqualTo(shared);
    }

    [Test]
    public async Task ApplyAsync_Should_Leave_A_Stable_Shared_File_Untouched_For_A_Custom_Channel()
    {
        var paths = ChannelPaths(CustomChannel);
        Seed(paths.LegacyRegistrationFiles[1], ServiceRegistrationData.StableRelease);

        await Migration().ApplyAsync(Select(CustomChannel), paths, CancellationToken.None);

        await Assert.That(_fileSystem.File.Exists(paths.RegistrationFile)).IsFalse();
        await Assert.That(_fileSystem.File.ReadAllText(paths.LegacyRegistrationFiles[1])).IsEqualTo(ServiceRegistrationData.StableRelease);
    }

    [Test]
    public async Task ApplyAsync_Should_Keep_An_Existing_Target_Without_Truncating_It()
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyRegistrationFiles[0], ServiceRegistrationData.DevPrerelease);
        Seed(paths.RegistrationFile, ServiceRegistrationData.Minimal);

        await Migration().ApplyAsync(Select(Channel), paths, CancellationToken.None);

        await Assert.That(_fileSystem.File.ReadAllText(paths.RegistrationFile)).IsEqualTo(ServiceRegistrationData.Minimal);
    }

    [Test]
    public async Task ApplyAsync_Should_Ignore_A_Missing_Target_Directory_And_Create_None()
    {
        var paths = ChannelPaths(Channel);
        var stateDirectory = _fileSystem.Path.GetDirectoryName(paths.RegistrationFile)!;
        var elsewhere = Path("elsewhere", "legacy.json");
        Seed(elsewhere, ServiceRegistrationData.DevPrerelease);
        var detached = paths with { LegacyRegistrationFiles = [elsewhere] };

        // The state directory was never created: the CLI's Global layer would have made it, the
        // SDK must neither create it nor fail the lookup over its absence.
        await Migration().ApplyAsync(Select(Channel), detached, CancellationToken.None);

        await Assert.That(_fileSystem.Directory.Exists(stateDirectory)).IsFalse();
    }

    [Test]
    public async Task ApplyAsync_Should_Ignore_Any_Other_Write_Failure()
    {
        var paths = ChannelPaths(Channel);
        var failing = Substitute.For<IServiceFileSystem>();
        failing.TryReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes(ServiceRegistrationData.DevPrerelease));
        failing.TryCreateExclusiveAsync(Arg.Any<string>(), Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>())
            .ThrowsAsync(new UnauthorizedAccessException("read-only state directory"));

        await new ServiceMigration(failing)
            .ApplyAsync(Select(Channel), paths, CancellationToken.None);

        _ = failing.Received(1).TryCreateExclusiveAsync(paths.RegistrationFile, Arg.Any<ReadOnlyMemory<byte>>(), Arg.Any<CancellationToken>());
    }

    [Test]
    public async Task ApplyAsync_Should_Do_Nothing_For_A_Direct_File_Or_The_Null_Channel()
    {
        var direct = Path("elsewhere", "registration.json");
        var fileSystem = Substitute.For<IServiceFileSystem>();

        var migration = new ServiceMigration(fileSystem);
        await migration.ApplyAsync(Select(null, registrationFilePath: direct), new ServicePaths(direct, null, [], null), CancellationToken.None);
        await migration.ApplyAsync(Select(null), ChannelPaths(null), CancellationToken.None);

        _ = fileSystem.DidNotReceiveWithAnyArgs().TryReadAllBytesAsync(default!, default);
    }

    [Test]
    public async Task ApplyAsync_Should_Copy_A_Valid_Legacy_Config_Without_A_Version_Gate()
    {
        var paths = ChannelPaths(Channel);
        var config = new FixtureLoader().LoadJson("BackgroundService.service-config-env.json");
        Seed(paths.LegacyConfigFile!, config);

        await Migration().ApplyAsync(Select(Channel), paths, CancellationToken.None);

        await Assert.That(_fileSystem.File.ReadAllText(paths.ConfigFile!)).IsEqualTo(config);
        await Assert.That(_fileSystem.File.Exists(paths.LegacyConfigFile!)).IsTrue();
    }

    [Test]
    public async Task ApplyAsync_Should_Not_Copy_An_Invalid_Legacy_Config()
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyConfigFile!, ServiceConfigData.PortAboveRange);

        await Migration().ApplyAsync(Select(Channel), paths, CancellationToken.None);

        await Assert.That(_fileSystem.File.Exists(paths.ConfigFile!)).IsFalse();
    }

    [Test]
    [Arguments(ServiceConfigData.RemoteRoute)]
    [Arguments(ServiceConfigData.RemoteRouteWithExtraMembers)]
    public async Task ApplyAsync_Should_Copy_A_Legacy_Config_With_A_Current_Remote(string config)
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyConfigFile!, config);

        await Migration().ApplyAsync(Select(Channel), paths, CancellationToken.None);

        await Assert.That(_fileSystem.File.ReadAllText(paths.ConfigFile!)).IsEqualTo(config);
    }

    /// <summary>The CLI's <c>migrateConfig</c> gates on the current decode alone, so a boolean <c>remote</c> stays behind.</summary>
    [Test]
    [Arguments(ServiceConfigData.LegacyRemoteTrue)]
    [Arguments(ServiceConfigData.LegacyRemoteFalse)]
    public async Task ApplyAsync_Should_Not_Copy_A_Legacy_Config_With_A_Boolean_Remote(string config)
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyConfigFile!, config);

        await Migration().ApplyAsync(Select(Channel), paths, CancellationToken.None);

        await Assert.That(_fileSystem.File.Exists(paths.ConfigFile!)).IsFalse();
    }

    [Test]
    public async Task ApplyAsync_Should_Rethrow_Caller_Cancellation()
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyRegistrationFiles[0], ServiceRegistrationData.DevPrerelease);
        var cancelled = new CancellationToken(canceled: true);

        _ = await Assert
            .That(async () => await Migration().ApplyAsync(Select(Channel), paths, cancelled))
            .Throws<OperationCanceledException>();
    }

    private ServiceMigration Migration() =>
        new(new TestablyServiceFileSystem(_fileSystem));

    private ServicePaths ChannelPaths(string? channel)
    {
        var environment = Substitute.For<IServiceEnvironment>();
        environment.GetEnvironmentVariable("XDG_STATE_HOME").Returns(Path("state"));
        environment.GetEnvironmentVariable("XDG_CONFIG_HOME").Returns(Path("config"));
        return new ServicePathResolver(environment).Resolve(Select(channel));
    }

    private static ServiceSelection Select(string? channel, string? registrationFilePath = null, string? installedVersion = null) =>
        ServiceSelection.Snapshot(new OpenCodeServerDiscoverOptions
        {
            Channel = channel,
            RegistrationFilePath = registrationFilePath,
            InstalledVersion = installedVersion,
        });

    private void Seed(string path, string content)
    {
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(path)!);
        _fileSystem.File.WriteAllText(path, content);
    }

    private string Path(params string[] segments) =>
        _fileSystem.Path.Combine([_fileSystem.Path.GetTempPath(), "service-migration", .. segments]);
}
