using System.Text;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using Testably.Abstractions.Testing;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Registration;

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
    [Arguments(false)]
    [Arguments(true)]
    public async Task ApplyAsync_Should_Copy_Invalid_Utf8_In_A_Known_String_As_Decoded_Text(bool registration)
    {
        await AssertDecodedDonorAsync(registration,
            registration ? ServiceConfigWriteData.InvalidRegistrationId : ServiceConfigWriteData.InvalidHostname,
            Encoding.UTF8.GetBytes(registration ? ServiceConfigWriteData.DecodedRegistrationId : ServiceConfigWriteData.DecodedHostname));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ApplyAsync_Should_Not_Copy_A_Double_Bom_Donor(bool registration)
    {
        var bytes = registration
            ? ServiceConfigWriteData.DoubleBomRegistration : ServiceConfigWriteData.DoubleBomDonor;
        await AssertDecodedDonorAsync(registration, bytes, expected: null);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ApplyAsync_Should_Not_Copy_Donor_Names_That_Collide_After_Decoding(bool registration)
    {
        var bytes = registration
            ? ServiceConfigWriteData.CollidingRegistrationNames : ServiceConfigWriteData.CollidingDonorNames;
        await AssertDecodedDonorAsync(registration, bytes, expected: null);
    }

    private async Task AssertDecodedDonorAsync(bool registration, byte[] bytes, byte[]? expected)
    {
        var paths = ChannelPaths("beta");
        var donor = registration ? paths.LegacyRegistrationFiles[0] : paths.LegacyConfigFile!;
        var target = registration ? paths.RegistrationFile : paths.ConfigFile!;
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(donor)!);
        var files = new TestablyServiceFileSystem(_fileSystem);
        _ = await files.TryCreateExclusiveAsync(donor, bytes, CancellationToken.None);

        await Migration().ApplyAsync(Select("beta"), paths, CancellationToken.None);

        var copied = await files.TryReadAllBytesAsync(target, CancellationToken.None);
        if (expected is null)
        {
            await Assert.That(copied).IsNull();
        }
        else
        {
            await Assert.That(copied).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }

        await Assert.That(await files.TryReadAllBytesAsync(donor, CancellationToken.None))
            .IsEquivalentTo(bytes, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ApplyAsync_Should_Write_A_Legacy_Config_As_The_Text_The_Cli_Decodes(bool bom)
    {
        var paths = ChannelPaths("beta");
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(paths.LegacyConfigFile!)!);
        var files = new TestablyServiceFileSystem(_fileSystem);
        _ = await files.TryCreateExclusiveAsync(paths.LegacyConfigFile!,
            ServiceConfigWriteData.Donor(ServiceConfigWriteData.Config, bom), CancellationToken.None);

        await Migration().ApplyAsync(Select("beta"), paths, CancellationToken.None);

        await Assert.That(await files.TryReadAllBytesAsync(paths.ConfigFile!, CancellationToken.None))
            .IsEquivalentTo(ServiceConfigWriteData.ConfigDecoded(bom), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task ApplyAsync_Should_Write_A_Legacy_Registration_As_The_Text_The_Cli_Decodes(bool bom)
    {
        var paths = ChannelPaths("beta");
        _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(paths.LegacyRegistrationFiles[0])!);
        var files = new TestablyServiceFileSystem(_fileSystem);
        _ = await files.TryCreateExclusiveAsync(paths.LegacyRegistrationFiles[0],
            ServiceConfigWriteData.Donor(ServiceConfigWriteData.Registration, bom), CancellationToken.None);

        await Migration().ApplyAsync(Select("beta"), paths, CancellationToken.None);

        await Assert.That(await files.TryReadAllBytesAsync(paths.RegistrationFile, CancellationToken.None))
            .IsEquivalentTo(ServiceConfigWriteData.RegistrationDecoded(bom), CollectionOrdering.Matching);
    }

    [Test]
    [Arguments("BackgroundService.registration-dev-prerelease.json")]
    [Arguments(ServiceRegistrationData.DevPrereleaseDotted)]
    public async Task ApplyAsync_Should_Copy_A_Legacy_Registration_Whose_Version_Belongs_To_The_Channel(string donor)
    {
        donor = donor == "BackgroundService.registration-dev-prerelease.json" ? new FixtureLoader().LoadJson(donor) : donor;
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyRegistrationFiles[0], donor);

        await Migration().ApplyAsync(Select(Channel), paths, CancellationToken.None);

        await Assert.That(paths.RegistrationFile).Satisfies(_fileSystem.File.Exists);
        await Assert.That(_fileSystem.File.ReadAllText(paths.RegistrationFile)).IsEqualTo(donor);
        await Assert.That(paths.LegacyRegistrationFiles[0]).Satisfies(_fileSystem.File.Exists);
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

        await AssertFileAbsentAsync(paths.RegistrationFile);
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

        await AssertFileAbsentAsync(paths.RegistrationFile);
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

        await AssertFileAbsentAsync(paths.RegistrationFile);
        await Assert.That(_fileSystem.File.ReadAllText(paths.LegacyRegistrationFiles[1])).IsEqualTo(ServiceRegistrationData.StableRelease);
    }

    [Test]
    public async Task ApplyAsync_Should_Keep_An_Existing_Target_Without_Truncating_It()
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyRegistrationFiles[0], new FixtureLoader().LoadJson("BackgroundService.registration-dev-prerelease.json"));
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
        Seed(elsewhere, new FixtureLoader().LoadJson("BackgroundService.registration-dev-prerelease.json"));
        var detached = paths with { LegacyRegistrationFiles = [elsewhere] };

        // The state directory was never created: the CLI's Global layer would have made it, the
        // SDK must neither create it nor fail the lookup over its absence.
        await Migration().ApplyAsync(Select(Channel), detached, CancellationToken.None);

        await Assert.That(stateDirectory).Satisfies(path => !_fileSystem.Directory.Exists(path));
    }

    [Test]
    public async Task ApplyAsync_Should_Ignore_Any_Other_Write_Failure()
    {
        var paths = ChannelPaths(Channel);
        var failing = Substitute.For<IServiceFileSystem>();
        failing.TryReadAllBytesAsync(Arg.Any<string>(), Arg.Any<CancellationToken>())
            .Returns(Encoding.UTF8.GetBytes(new FixtureLoader().LoadJson("BackgroundService.registration-dev-prerelease.json")));
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
        await Assert.That(paths.LegacyConfigFile!).Satisfies(_fileSystem.File.Exists);
    }

    [Test]
    public async Task ApplyAsync_Should_Not_Copy_An_Invalid_Legacy_Config()
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyConfigFile!, ServiceConfigData.PortAboveRange);

        await Migration().ApplyAsync(Select(Channel), paths, CancellationToken.None);

        await AssertFileAbsentAsync(paths.ConfigFile!);
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

        await AssertFileAbsentAsync(paths.ConfigFile!);
    }

    [Test]
    public async Task ApplyAsync_Should_Rethrow_Caller_Cancellation()
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyRegistrationFiles[0], new FixtureLoader().LoadJson("BackgroundService.registration-dev-prerelease.json"));
        var cancelled = new CancellationToken(canceled: true);

        _ = await Assert
            .That(async () => await Migration().ApplyAsync(Select(Channel), paths, cancelled))
            .Throws<OperationCanceledException>();
    }

    [Test]
    public async Task ApplyConfigAsync_Should_Copy_Only_The_Config_Donor()
    {
        var paths = ChannelPaths(Channel);
        Seed(paths.LegacyConfigFile!, ServiceConfigData.DisabledTrue);
        Seed(paths.LegacyRegistrationFiles[0], ServiceRegistrationData.DevPrereleaseDotted);

        await Migration().ApplyConfigAsync(paths, CancellationToken.None);

        await Assert.That(_fileSystem.Directory.GetFiles(_fileSystem.Path.GetDirectoryName(paths.ConfigFile!)!))
            .IsEquivalentTo([paths.LegacyConfigFile!, paths.ConfigFile!], CollectionOrdering.Matching);
        await Assert.That(_fileSystem.Directory.GetFiles(_fileSystem.Path.GetDirectoryName(paths.RegistrationFile)!))
            .IsEquivalentTo([paths.LegacyRegistrationFiles[0]], CollectionOrdering.Matching);
        await Assert.That(_fileSystem.File.ReadAllText(paths.ConfigFile!)).IsEqualTo(ServiceConfigData.DisabledTrue);
    }

    private ServiceMigration Migration() =>
        new(new TestablyServiceFileSystem(_fileSystem));

    private async Task AssertFileAbsentAsync(string file) =>
        await Assert.That(file).Satisfies(path => !_fileSystem.File.Exists(path));

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
