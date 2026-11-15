using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.BackgroundService.Registration;

/// <summary>
/// The strict boundary over the CLI's service config: the seven declared members are validated, the
/// persisted password and remote route are accepted and never surfaced, a legacy boolean
/// <c>remote</c> still reads but is not the current shape, and only the environment map comes out.
/// </summary>
public sealed class ServiceConfigReaderTests
{
    [Test]
    public async Task TryReadDocument_Should_Retain_Every_Declared_Value()
    {
        var document = ServiceConfigReader.TryReadDocument(Encoding.UTF8.GetBytes(new ServiceConfigScenario().Initial("all-members")!));

        await Assert.That(document).IsNotNull();
        await Assert.That(document.Disabled).IsTrue();
        await Assert.That(document.RemoteAccepted).IsTrue();
        await Assert.That(document.RemoteRoute).IsEqualTo("stored-route");
        await Assert.That(document.LegacyRemote).IsNull();
        await Assert.That(document.Hostname).IsEqualTo("127.0.0.2");
        await Assert.That(document.Port).IsEqualTo(5123);
        await Assert.That(document.Password).IsEqualTo("stored-password");
        await Assert.That(document.CorsOrigins).IsEquivalentTo(["https://b.example", "https://a.example"], CollectionOrdering.Matching);
        await Assert.That(document.Environment).IsEquivalentTo(new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["B"] = "two",
            ["10"] = "ten",
            ["XDG_STATE_HOME"] = "saved-state",
        }, CollectionOrdering.Any);
    }

    [Test]
    [Arguments(ServiceConfigData.RemoteRoute, null)]
    [Arguments(ServiceConfigData.LegacyRemoteTrue, true)]
    [Arguments(ServiceConfigData.LegacyRemoteFalse, false)]
    public async Task TryReadDocument_Should_Retain_The_Current_And_Legacy_Remote_Values(string json, bool? legacy)
    {
        var document = ServiceConfigReader.TryReadDocument(Bytes(json));

        await Assert.That(document).IsNotNull();
        await Assert.That(document.LegacyRemote).IsEqualTo(legacy);
        await Assert.That(document.RemoteRoute).IsEqualTo(legacy is null ? "0123456789abcdef" : null);
        await Assert.That(document.Environment!["A"]).IsEqualTo("1");
        await Assert.That(ServiceConfigReader.IsCurrent(Bytes(json))).IsEqualTo(legacy is null);
    }

    [Test]
    [Arguments("surrogate-route")]
    [Arguments("surrogate-cors")]
    [Arguments("repeated-member")]
    public async Task TryReadDocument_Should_Refuse_Undecodable_Strings_Without_Changing_The_Existing_Projections(string name)
    {
        using var corpus = JsonDocument.Parse(new FixtureLoader().LoadJson("BackgroundService.service-config-corpus.json"));
        var json = corpus.RootElement.EnumerateArray().Single(row => row.GetProperty("name").GetString() == name)
            .GetProperty("initial").GetString()!;
        var bytes = Bytes(json);

        await Assert.That(ServiceConfigReader.TryReadDocument(bytes)).IsNull();
        if (name == "repeated-member")
        {
            await Assert.That(ServiceConfigReader.TryReadEnvironment(bytes)).IsNull();
            await Assert.That(ServiceConfigReader.IsCurrent(bytes)).IsFalse();
        }
        else
        {
            var environment = ServiceConfigReader.TryReadEnvironment(bytes);
            await Assert.That(environment).IsNotNull();
            await Assert.That(environment["A"]).IsEqualTo("1");
            await Assert.That(ServiceConfigReader.IsCurrent(bytes)).IsTrue();
        }
    }

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
    [Arguments(ServiceConfigData.RemoteAccepted)]
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
    [Arguments(ServiceConfigData.RemoteAcceptedFalse)]
    [Arguments(ServiceConfigData.RemoteAcceptedNull)]
    [Arguments(ServiceConfigData.RemoteAcceptedAsString)]
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
    [Arguments(ServiceConfigData.RemoteAccepted)]
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
    [Arguments(ServiceConfigData.RemoteAcceptedFalse)]
    [Arguments(ServiceConfigData.PortAboveRange)]
    [Arguments(ServiceConfigData.Malformed)]
    public async Task IsCurrent_Should_Refuse_A_Legacy_Or_Invalid_Config(string json)
    {
        await Assert.That(ServiceConfigReader.IsCurrent(Bytes(json))).IsFalse();
    }

    private static byte[] Bytes(string json) => Encoding.UTF8.GetBytes(json);
}
