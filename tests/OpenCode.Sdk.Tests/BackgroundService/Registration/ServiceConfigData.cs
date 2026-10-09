namespace OpenCode.Sdk.Tests.BackgroundService.Registration;

/// <summary>
/// Service-config documents that isolate one construct each: the seven members the pinned CLI's
/// <c>ServiceConfig.Info</c> declares and the rejections the strict reader owns. The canonical
/// document with an environment map is the embedded fixture
/// <c>BackgroundService.service-config-env.json</c>.
/// </summary>
internal static class ServiceConfigData
{
    public const string ConfigPassword = "c0nf1g-p455w0rd";

    public const string Empty = "{}";

    public const string HostnameOnly = "{\"hostname\":\"0.0.0.0\"}";

    public const string DisabledTrue = "{\"disabled\":true}";

    public const string DisabledFalse = "{\"disabled\":false}";

    /// <summary>
    /// The exact bytes <c>opencode service set disabled true</c> writes over an empty config: the
    /// CLI serializes the whole config with two-space indentation and a trailing newline.
    /// </summary>
    public const string DisabledAsTheCliWritesIt = "{\n  \"disabled\": true\n}\n";

    public const string DisabledAsString = "{\"disabled\":\"true\"}";

    public const string DisabledNull = "{\"disabled\":null}";

    public const string DisabledAsNumber = "{\"disabled\":1}";

    public const string PortAtLowerBound = "{\"port\":1}";

    public const string PortAtUpperBound = "{\"port\":65535}";

    /// <summary>An integral port written with a zero fraction: <c>JSON.parse</c> reads 8080, a safe integer.</summary>
    public const string PortWithZeroFraction = "{\"port\":8080.0,\"env\":{\"A\":\"1\"}}";

    /// <summary>An integral port written in exponent notation: <c>JSON.parse</c> reads 8080, a safe integer.</summary>
    public const string PortInExponentNotation = "{\"port\":8.08e3,\"env\":{\"A\":\"1\"}}";

    public const string PortWithFraction = "{\"port\":8080.5}";

    /// <summary>2^53 written with a zero fraction: integral, but past <c>Number.MAX_SAFE_INTEGER</c>.</summary>
    public const string PortPastSafeInteger = "{\"port\":9007199254740992.0}";

    /// <summary><c>disabled</c> is validated and never read, so a disabled service still has its environment.</summary>
    public const string DisabledWithEnvironment = "{\"disabled\":true,\"env\":{\"A\":\"1\"}}";

    public const string PortZero = "{\"port\":0}";

    public const string PortAboveRange = "{\"port\":65536}";

    public const string PortAsString = "{\"port\":\"49374\"}";

    public const string CorsNotAnArray = "{\"cors\":\"http://localhost:5173\"}";

    public const string CorsWithNonString = "{\"cors\":[\"http://localhost:5173\",5173]}";

    public const string EnvWithNonStringValue = "{\"env\":{\"HTTPS_PROXY\":3128}}";

    public const string EnvNotAnObject = "{\"env\":[\"HTTPS_PROXY=x\"]}";

    public const string HostnameNotAString = "{\"hostname\":1}";

    public const string ArrayRoot = "[]";

    public const string Malformed = "{\"env\":{";

    /// <summary>The current <c>remote</c> shape: the generated route the service is served on.</summary>
    public const string RemoteRoute = "{\"remote\":{\"route\":\"0123456789abcdef\"},\"env\":{\"A\":\"1\"}}";

    /// <summary><c>Struct({ route: String })</c> ignores any other member of the object.</summary>
    public const string RemoteRouteWithExtraMembers = "{\"remote\":{\"route\":\"0123456789abcdef\",\"enabled\":true},\"env\":{\"A\":\"1\"}}";

    /// <summary>Remote access as earlier builds stored it: the CLI's legacy decode still reads it.</summary>
    public const string LegacyRemoteTrue = "{\"remote\":true,\"password\":\"kept\",\"env\":{\"A\":\"1\"}}";

    /// <summary>Remote access turned off, as earlier builds stored it.</summary>
    public const string LegacyRemoteFalse = "{\"remote\":false,\"env\":{\"A\":\"1\"}}";

    public const string RemoteAsNumber = "{\"remote\":5,\"env\":{\"A\":\"1\"}}";

    public const string RemoteNull = "{\"remote\":null,\"env\":{\"A\":\"1\"}}";

    public const string RemoteAsString = "{\"remote\":\"x\",\"env\":{\"A\":\"1\"}}";

    public const string RemoteWithoutRoute = "{\"remote\":{},\"env\":{\"A\":\"1\"}}";

    public const string RemoteRouteNotAString = "{\"remote\":{\"route\":1},\"env\":{\"A\":\"1\"}}";

    public const string UnknownMembersSkipped = "{\"env\":{\"A\":\"1\"},\"future\":{\"nested\":true}}";
}
