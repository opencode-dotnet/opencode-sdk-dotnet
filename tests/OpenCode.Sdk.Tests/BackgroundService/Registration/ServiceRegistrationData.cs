namespace OpenCode.Sdk.Tests.BackgroundService.Registration;

/// <summary>
/// Registration documents that isolate one construct each: the shape the pinned daemon writes and
/// every rejection the strict reader owns. The canonical modern document is the embedded fixture
/// <c>BackgroundService.registration-modern.json</c>; these are its variants.
/// </summary>
internal static class ServiceRegistrationData
{
    public const string Password = "s3cr3t-p455w0rd";

    public const string Minimal = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":48213}";

    public const string Passwordless = "{\"id\":\"srv_1\",\"version\":\"2.0.3\",\"url\":\"http://127.0.0.1:49374\",\"pid\":48213}";

    /// <summary>The same service as <see cref="Passwordless"/> with its password: one identity, two documents.</summary>
    public const string PasswordedTwin = "{\"id\":\"srv_1\",\"version\":\"2.0.3\",\"url\":\"http://127.0.0.1:49374\",\"pid\":48213,\"password\":\"s3cr3t-p455w0rd\"}";

    /// <summary>The same service as <see cref="Passwordless"/> under another pid: a different identity.</summary>
    public const string DuplicatePidResolved = "{\"id\":\"srv_1\",\"version\":\"2.0.3\",\"url\":\"http://127.0.0.1:49374\",\"pid\":48214}";

    public const string BlankPassword = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":48213,\"password\":\"  \"}";

    public const string EmptyPassword = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":48213,\"password\":\"\"}";

    public const string UnknownMembersSkipped = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":48213,\"future\":{\"nested\":[1,2]},\"flag\":true}";

    public const string ArrayRoot = "[{\"url\":\"http://127.0.0.1:49374\",\"pid\":48213}]";

    public const string MissingUrl = "{\"pid\":48213}";

    public const string MissingPid = "{\"url\":\"http://127.0.0.1:49374\"}";

    public const string RelativeUrl = "{\"url\":\"/api\",\"pid\":48213}";

    public const string NonHttpUrl = "{\"url\":\"ws://127.0.0.1:49374\",\"pid\":48213}";

    public const string ZeroPid = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":0}";

    public const string NegativePid = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":-1}";

    public const string PidAboveInt32 = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":2147483648}";

    public const string FractionalPid = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":48213.5}";

    /// <summary>A fraction that truncates to a valid pid: refused as a whole, never rounded down to 1.</summary>
    public const string FractionalPidAboveOne = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":1.5}";

    /// <summary>An integral pid written with a zero fraction: <c>JSON.parse</c> reads 1234, a safe integer.</summary>
    public const string PidWithZeroFraction = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":1234.0}";

    /// <summary>An integral pid written in exponent notation: <c>JSON.parse</c> reads 1234, a safe integer.</summary>
    public const string PidInExponentNotation = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":1.234e3}";

    public const string StringPid = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":\"48213\"}";

    public const string NumericUrl = "{\"url\":49374,\"pid\":48213}";

    public const string ObjectVersion = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":48213,\"version\":{}}";

    public const string DuplicateUrl = "{\"url\":\"http://127.0.0.1:1\",\"url\":\"http://127.0.0.1:49374\",\"pid\":48213}";

    public const string DuplicatePid = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":1,\"pid\":48213}";

    public const string Malformed = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":48213";

    public const string TrailingContent = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":48213}{}";

    /// <summary>A pre-release registration a <c>dev</c> build wrote, the shape the channel-prefix migration arm admits.</summary>
    public const string DevPrerelease = "{\"version\":\"0.0.0-dev-19646\",\"url\":\"http://127.0.0.1:49374\",\"pid\":48213,\"password\":\"dev-p455\"}";

    /// <summary>A custom-channel (<c>preview/a</c>) donor under the hashed legacy name; the migration prefers it.</summary>
    public const string CustomChannelHashedDonor = "{\"version\":\"0.0.0-preview/a-1234\",\"url\":\"http://127.0.0.1:1\",\"pid\":1}";

    /// <summary>The same channel's donor under the shared <c>service.json</c> name; the fallback when the hashed donor is absent.</summary>
    public const string CustomChannelSharedDonor = "{\"version\":\"0.0.0-preview/a-5678\",\"url\":\"http://127.0.0.1:2\",\"pid\":2}";

    public const string DevPrereleaseDotted = "{\"version\":\"0.0.0-dev-19646.2\",\"url\":\"http://127.0.0.1:49374\",\"pid\":48213}";

    public const string DevPrereleaseThreeSegments = "{\"version\":\"0.0.0-dev-19646.2.1\",\"url\":\"http://127.0.0.1:49374\",\"pid\":48213}";

    public const string DevPrereleaseWrongChannel = "{\"version\":\"0.0.0-beta-19507\",\"url\":\"http://127.0.0.1:49374\",\"pid\":48213}";

    /// <summary>A stable release registration: never claimed by a prefix arm, only by an exact installed version.</summary>
    public const string StableRelease = "{\"version\":\"1.2.3\",\"url\":\"http://127.0.0.1:49374\",\"pid\":48213}";

    public const string Versionless = "{\"url\":\"http://127.0.0.1:49374\",\"pid\":48213}";
}
