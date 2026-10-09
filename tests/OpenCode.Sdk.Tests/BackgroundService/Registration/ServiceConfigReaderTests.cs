using System.Text;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.BackgroundService.Registration;

/// <summary>
/// The strict boundary over the CLI's service config: the seven declared members are validated, the
/// persisted password and remote route are accepted and never surfaced, a legacy boolean
/// <c>remote</c> still reads but is not the current shape, and only the environment map comes out.
/// </summary>
public sealed class ServiceConfigReaderTests
{
    [Test]
    public async Task TryReadEnvironment_Should_Return_The_Environment_Map_Of_The_Pinned_Config()
    {
        var environment = ServiceConfigReader.TryReadEnvironment(Bytes(new FixtureLoader().LoadJson("BackgroundService.service-config-env.json")));

        await Assert.That(environment).IsNotNull();
        await Assert.That(environment.Count).IsEqualTo(2);
        await Assert.That(environment["OPENCODE_DISABLE_MODELS_FETCH"]).IsEqualTo("1");
        await Assert.That(environment["HTTPS_PROXY"]).IsEqualTo("http://proxy.internal:3128");
        await Assert.That(environment.Values).DoesNotContain(ServiceConfigData.ConfigPassword);
    }

    [Test]
    [Arguments(ServiceConfigData.Empty)]
    [Arguments(ServiceConfigData.HostnameOnly)]
    [Arguments(ServiceConfigData.DisabledTrue)]
    [Arguments(ServiceConfigData.DisabledFalse)]
    [Arguments(ServiceConfigData.DisabledAsTheCliWritesIt)]
    [Arguments(ServiceConfigData.PortAtLowerBound)]
    [Arguments(ServiceConfigData.PortAtUpperBound)]
    public async Task TryReadEnvironment_Should_Accept_A_Valid_Config_Without_Environment_As_Empty(string json)
    {
        var environment = ServiceConfigReader.TryReadEnvironment(Bytes(json));

        await Assert.That(environment).IsNotNull();
        await Assert.That(environment).IsEmpty();
    }

    [Test]
    public async Task TryReadEnvironment_Should_Skip_Unknown_Members()
    {
        var environment = ServiceConfigReader.TryReadEnvironment(Bytes(ServiceConfigData.UnknownMembersSkipped));

        await Assert.That(environment).IsNotNull();
        await Assert.That(environment["A"]).IsEqualTo("1");
    }

    /// <summary>
    /// The CLI's schema reads <c>port</c> as <c>Number.isSafeInteger</c> over the value <c>JSON.parse</c>
    /// produced, so the notation an integer is written in does not matter, and <c>disabled</c> never
    /// gates the environment. <c>remote</c> is the route object, whose other members the decode
    /// ignores, or the boolean earlier builds stored, which the CLI's legacy decode reads with the
    /// rest of the document.
    /// </summary>
    [Test]
    [Arguments(ServiceConfigData.PortWithZeroFraction)]
    [Arguments(ServiceConfigData.PortInExponentNotation)]
    [Arguments(ServiceConfigData.DisabledWithEnvironment)]
    [Arguments(ServiceConfigData.RemoteRoute)]
    [Arguments(ServiceConfigData.RemoteRouteWithExtraMembers)]
    [Arguments(ServiceConfigData.LegacyRemoteTrue)]
    [Arguments(ServiceConfigData.LegacyRemoteFalse)]
    public async Task TryReadEnvironment_Should_Return_The_Environment_Of_A_Config_The_Cli_Accepts(string json)
    {
        var environment = ServiceConfigReader.TryReadEnvironment(Bytes(json));

        await Assert.That(environment).IsNotNull();
        await Assert.That(environment.Count).IsEqualTo(1);
        await Assert.That(environment["A"]).IsEqualTo("1");
    }

    [Test]
    [Arguments(ServiceConfigData.PortZero)]
    [Arguments(ServiceConfigData.PortAboveRange)]
    [Arguments(ServiceConfigData.PortWithFraction)]
    [Arguments(ServiceConfigData.PortPastSafeInteger)]
    [Arguments(ServiceConfigData.PortAsString)]
    [Arguments(ServiceConfigData.CorsNotAnArray)]
    [Arguments(ServiceConfigData.CorsWithNonString)]
    [Arguments(ServiceConfigData.EnvWithNonStringValue)]
    [Arguments(ServiceConfigData.EnvNotAnObject)]
    [Arguments(ServiceConfigData.HostnameNotAString)]
    [Arguments(ServiceConfigData.DisabledAsString)]
    [Arguments(ServiceConfigData.DisabledNull)]
    [Arguments(ServiceConfigData.DisabledAsNumber)]
    [Arguments(ServiceConfigData.RemoteAsNumber)]
    [Arguments(ServiceConfigData.RemoteNull)]
    [Arguments(ServiceConfigData.RemoteAsString)]
    [Arguments(ServiceConfigData.RemoteWithoutRoute)]
    [Arguments(ServiceConfigData.RemoteRouteNotAString)]
    [Arguments(ServiceConfigData.ArrayRoot)]
    [Arguments(ServiceConfigData.Malformed)]
    public async Task TryReadEnvironment_Should_Treat_An_Invalid_Config_As_Absent(string json)
    {
        var environment = ServiceConfigReader.TryReadEnvironment(Bytes(json));

        await Assert.That(environment).IsNull();
    }

    /// <summary>A hand-edited config saved by an editor that writes a BOM: the CLI's decoder strips it, so its <c>env</c> applies.</summary>
    [Test]
    public async Task TryReadEnvironment_Should_Read_A_Bom_Prefixed_Config()
    {
        var environment = ServiceConfigReader.TryReadEnvironment([0xEF, 0xBB, 0xBF, .. Bytes("{\"env\":{\"A\":\"1\"}}")]);

        await Assert.That(environment).IsNotNull();
        await Assert.That(environment["A"]).IsEqualTo("1");
    }

    [Test]
    public async Task TryReadEnvironment_Should_Treat_Invalid_Utf8_In_A_String_As_Absent()
    {
        // 0xC3 opens a two-byte sequence that 0x28 ('(') cannot continue.
        byte[] document = [.. Bytes("{\"env\":{\"A\":\""), 0xC3, 0x28, .. Bytes("\"}}")];

        var environment = ServiceConfigReader.TryReadEnvironment(document);

        await Assert.That(environment).IsNull();
    }

    [Test]
    [Arguments(ServiceConfigData.Empty)]
    [Arguments(ServiceConfigData.DisabledWithEnvironment)]
    [Arguments(ServiceConfigData.RemoteRoute)]
    [Arguments(ServiceConfigData.RemoteRouteWithExtraMembers)]
    public async Task IsCurrent_Should_Accept_A_Config_In_The_Current_Shape(string json)
    {
        await Assert.That(ServiceConfigReader.IsCurrent(Bytes(json))).IsTrue();
    }

    /// <summary>
    /// The CLI's <c>migrateConfig</c> gates on the current decode alone: a legacy boolean
    /// <c>remote</c> reads, but is not the current shape, and an invalid config is neither.
    /// </summary>
    [Test]
    [Arguments(ServiceConfigData.LegacyRemoteTrue)]
    [Arguments(ServiceConfigData.LegacyRemoteFalse)]
    [Arguments(ServiceConfigData.RemoteNull)]
    [Arguments(ServiceConfigData.RemoteWithoutRoute)]
    [Arguments(ServiceConfigData.PortAboveRange)]
    [Arguments(ServiceConfigData.Malformed)]
    public async Task IsCurrent_Should_Refuse_A_Legacy_Or_Invalid_Config(string json)
    {
        await Assert.That(ServiceConfigReader.IsCurrent(Bytes(json))).IsFalse();
    }

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
}
