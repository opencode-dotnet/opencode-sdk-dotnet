using NSubstitute;
using OpenCode.Sdk.Internal;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tests.BackgroundService.Registration;

/// <summary>
/// The channel, XDG, and legacy-donor rules of the pinned CLI's <c>service-config.ts</c>, resolved
/// against a substituted environment; no filesystem is touched, the paths are only composed.
/// </summary>
public sealed class ServicePathResolverTests
{
    private readonly MockFileSystem _fileSystem = new();
    private readonly IServiceEnvironment _environment = Substitute.For<IServiceEnvironment>();

    /// <summary><c>sha1("dev")</c> as lowercase hex, the way upstream's <c>Hash.fast</c> renders it.</summary>
    private const string DevLegacyName = "service-34c6fceca75e456f25e7e99531e2425c6c1de443.json";

    /// <summary><c>sha1("preview/a")</c>: the raw channel string is hashed, not the sanitized filename form.</summary>
    private const string PreviewLegacyName = "service-72b23bd4755536db5fbecc0c9c53c5e9d4b83cbe.json";

    [Test]
    public async Task Resolve_Should_Use_The_Shared_Registration_With_No_Donor_For_The_Default()
    {
        var state = Root("state");
        var config = Root("config");
        Environment(("XDG_STATE_HOME", state), ("XDG_CONFIG_HOME", config));

        var paths = Resolver().Resolve(Select(null, null, null, null));

        await Assert.That(paths.RegistrationFile).IsEqualTo(Path(state, "opencode", "service.json"));
        await Assert.That(paths.ConfigFile).IsEqualTo(Path(config, "opencode", "service.json"));
        await Assert.That(paths.LegacyRegistrationFiles).IsEmpty();
        await Assert.That(paths.LegacyConfigFile).IsNull();
    }

    [Test]
    public async Task Resolve_Should_Use_The_User_Profile_When_Xdg_Is_Empty()
    {
        var home = Root("home");
        Environment(("XDG_STATE_HOME", ""), ("XDG_CONFIG_HOME", null));
        _environment.UserProfile.Returns(home);

        var paths = Resolver().Resolve(Select(null, null, null, null));

        await Assert.That(paths.RegistrationFile).IsEqualTo(Path(home, ".local", "state", "opencode", "service.json"));
        await Assert.That(paths.ConfigFile).IsEqualTo(Path(home, ".config", "opencode", "service.json"));
    }

    [Test]
    public async Task Resolve_Should_Replace_The_Config_Root_With_OPENCODE_CONFIG_DIR()
    {
        var state = Root("state");
        var configDir = Root("cfg");
        Environment(("XDG_STATE_HOME", state), ("XDG_CONFIG_HOME", Root("ignored")), ("OPENCODE_CONFIG_DIR", configDir));

        var paths = Resolver().Resolve(Select("dev", null, null, null));

        // The variable is the whole root: no "opencode" segment (packages/util/src/global.ts:79).
        await Assert.That(paths.ConfigFile).IsEqualTo(Path(configDir, "service.json"));
        await Assert.That(paths.LegacyConfigFile).IsEqualTo(Path(configDir, DevLegacyName));
        await Assert.That(paths.RegistrationFile).IsEqualTo(Path(state, "opencode", "service.json"));
    }

    [Test]
    public async Task Resolve_Should_Throw_OpenCodeServerException_When_The_User_Profile_Is_Unavailable()
    {
        Environment(("XDG_STATE_HOME", null), ("XDG_CONFIG_HOME", null));
        _environment.UserProfile.Returns((string?)null);

        _ = await Assert
            .That(() => Resolver().Resolve(Select(null, null, null, null)))
            .Throws<OpenCodeServerException>();
    }

    [Test]
    public async Task Resolve_Should_Not_Need_The_User_Profile_When_Both_Xdg_Roots_Are_Set()
    {
        Environment(("XDG_STATE_HOME", Root("state")), ("XDG_CONFIG_HOME", Root("config")));
        _environment.UserProfile.Returns((string?)null);

        var paths = Resolver().Resolve(Select(null, null, null, null));

        await Assert.That(paths.RegistrationFile).IsEqualTo(Path(Root("state"), "opencode", "service.json"));
    }

    [Test]
    [Arguments("latest", "service.json")]
    [Arguments("dev", "service.json")]
    [Arguments("beta", "service.json")]
    [Arguments("next", "service.json")]
    [Arguments("local", "service-local.json")]
    [Arguments("preview-a", "service-preview-a.json")]
    [Arguments("preview/a", "service-preview-a.json")]
    public async Task Resolve_Should_Name_Release_Local_And_Custom_Channels_Like_The_Cli(string channel, string fileName)
    {
        var state = Root("state");
        Environment(("XDG_STATE_HOME", state), ("XDG_CONFIG_HOME", Root("config")));

        var paths = Resolver().Resolve(Select(channel, null, null, null));

        await Assert.That(paths.RegistrationFile).IsEqualTo(Path(state, "opencode", fileName));
    }

    [Test]
    public async Task Resolve_Should_Sanitize_Custom_Channels_By_Utf16_Unit()
    {
        var state = Root("state");
        Environment(("XDG_STATE_HOME", state), ("XDG_CONFIG_HOME", Root("config")));

        // U+1F600 is one code point but two UTF-16 units; the CLI's regex has no `u` flag.
        var paths = Resolver().Resolve(Select("pre\U0001F600view", null, null, null));

        await Assert.That(paths.RegistrationFile).IsEqualTo(Path(state, "opencode", "service-pre--view.json"));
    }

    [Test]
    public async Task Resolve_Should_Produce_The_Sha1_Legacy_Channel_Name()
    {
        var state = Root("state");
        var config = Root("config");
        Environment(("XDG_STATE_HOME", state), ("XDG_CONFIG_HOME", config));

        var paths = Resolver().Resolve(Select("dev", null, null, null));

        string[] expected = [Path(state, "opencode", DevLegacyName)];
        await Assert.That(paths.LegacyRegistrationFiles).IsEquivalentTo(expected);
        await Assert.That(paths.LegacyConfigFile).IsEqualTo(Path(config, "opencode", DevLegacyName));
    }

    [Test]
    public async Task Resolve_Should_Order_Registration_Donors_Hashed_Then_Shared_For_A_Custom_Channel()
    {
        var state = Root("state");
        var config = Root("config");
        Environment(("XDG_STATE_HOME", state), ("XDG_CONFIG_HOME", config));

        var paths = Resolver().Resolve(Select("preview/a", null, null, null));

        await Assert.That(paths.RegistrationFile).IsEqualTo(Path(state, "opencode", "service-preview-a.json"));
        string[] expected = [Path(state, "opencode", PreviewLegacyName), Path(state, "opencode", "service.json")];
        await Assert.That(paths.LegacyRegistrationFiles).IsEquivalentTo(expected);
        await Assert.That(paths.LegacyConfigFile).IsEqualTo(Path(config, "opencode", PreviewLegacyName));
    }

    [Test]
    public async Task Resolve_Should_Give_A_Local_Channel_No_Legacy_Donor()
    {
        Environment(("XDG_STATE_HOME", Root("state")), ("XDG_CONFIG_HOME", Root("config")));

        var paths = Resolver().Resolve(Select("local", null, null, null));

        await Assert.That(paths.LegacyRegistrationFiles).IsEmpty();
        await Assert.That(paths.LegacyConfigFile).IsNull();
    }

    [Test]
    public async Task Resolve_Should_Give_The_Latest_Channel_No_Legacy_Donor()
    {
        Environment(("XDG_STATE_HOME", Root("state")), ("XDG_CONFIG_HOME", Root("config")));

        var paths = Resolver().Resolve(Select("latest", null, null, null));

        await Assert.That(paths.LegacyRegistrationFiles).IsEmpty();
        await Assert.That(paths.LegacyConfigFile).IsNull();
    }

    [Test]
    public async Task Resolve_Should_Bypass_Roots_And_Donors_For_A_Direct_File()
    {
        var direct = Path(Root("elsewhere"), "registration.json");
        _environment.UserProfile.Returns((string?)null);

        var paths = Resolver().Resolve(Select(null, direct, null, null));

        await Assert.That(paths.RegistrationFile).IsEqualTo(direct);
        await Assert.That(paths.ConfigFile).IsNull();
        await Assert.That(paths.LegacyRegistrationFiles).IsEmpty();
        await Assert.That(paths.LegacyConfigFile).IsNull();
        _ = _environment.DidNotReceive().GetEnvironmentVariable(Arg.Any<string>());
    }

    private ServicePathResolver Resolver() => new(_environment);

    private void Environment(params (string Name, string? Value)[] variables)
    {
        foreach (var (name, value) in variables)
        {
            _environment.GetEnvironmentVariable(name).Returns(value);
        }
    }

    private string Root(string name) => _fileSystem.Path.Combine(_fileSystem.Path.GetTempPath(), "service-paths", name);

    private string Path(params string[] segments) => _fileSystem.Path.Combine(segments);

    private static ServiceSelection Select(string? channel, string? registrationFilePath, string? installedVersion, string? expectedVersion) =>
        ServiceSelection.Snapshot(new OpenCodeServerDiscoverOptions
        {
            Channel = channel,
            RegistrationFilePath = registrationFilePath,
            InstalledVersion = installedVersion,
            ExpectedVersion = expectedVersion,
        });
}
