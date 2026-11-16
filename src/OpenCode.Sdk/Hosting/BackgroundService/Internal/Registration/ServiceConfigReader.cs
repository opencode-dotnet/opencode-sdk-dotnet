using System.Text.Json;
using System.Text.Unicode;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The strict boundary over the CLI's service config (<c>ServiceConfig.Info</c> at the pin:
/// optional <c>disabled</c> boolean, <c>remote</c> object with a string <c>route</c>,
/// <c>remoteAccepted</c> literal <c>true</c>, <c>hostname</c> string, <c>port</c> integer 1..65535,
/// <c>password</c> string, <c>cors</c> string array, <c>env</c> string map). The daemon persists its
/// generated password here and the CLI's remote-access commands persist the remote route.
/// <see cref="TryReadDocument"/> materializes every member for service-configuration operations,
/// which keep the password and route inside the SDK: the public config view exposes neither.
/// <see cref="TryReadEnvironment"/> and <see cref="IsCurrent"/> keep their narrower projection for
/// Ensure and migration. Earlier builds stored
/// <c>remote</c> as a boolean: the CLI's <c>read()</c> still accepts that legacy shape and rewrites it
/// in place; the SDK's config store does the same for every service-configuration operation that
/// reads through it, ReadAsync included, and Discovery, Ensure and Stop read the document as it is,
/// while its <c>migrateConfig</c> copies only the current shape. Any other document is absent
/// config, as the CLI's own read turns a document that fails both decodes into an empty config.
/// </summary>
internal static class ServiceConfigReader
{
    private const int MaxPort = 65_535;

    /// <summary>The text produced by the CLI's TextDecoder, including maximal-subpart replacement on downlevel targets.</summary>
    /// <param name="utf8">The original file bytes.</param>
    /// <returns>The decoded text without one leading UTF-8 byte-order mark.</returns>
    internal static string DecodeCliText(ReadOnlySpan<byte> utf8)
    {
        if (utf8.StartsWith("\uFEFF"u8))
        {
            utf8 = utf8[3..];
        }

        var characters = new char[utf8.Length];
        _ = Utf8.ToUtf16(utf8, characters, out _, out var written, replaceInvalidSequences: true, isFinalBlock: true);
        return new string(characters, 0, written);
    }

    /// <summary>Proves the CLI rejects both shapes; a parser limit is never proof.</summary>
    /// <param name="utf8">The original file bytes.</param>
    /// <returns>True only for a completed syntax or shape rejection.</returns>
    public static bool IsProvenUnreadableByCli(ReadOnlySpan<byte> utf8) =>
        IsProvenUnreadable(utf8, static root => IsCliShape(root, admitLegacy: true));

    /// <summary>Proves the CLI's migration rejects the current shape alone.</summary>
    /// <param name="utf8">The original donor bytes.</param>
    /// <returns>True only for a completed current-shape rejection.</returns>
    public static bool IsProvenUnreadableByCliAsCurrent(ReadOnlySpan<byte> utf8) =>
        IsProvenUnreadable(utf8, static root => IsCliShape(root, admitLegacy: false));

    private static bool IsProvenUnreadable(ReadOnlySpan<byte> utf8, Func<JsonElement, bool> accepts)
    {
        var text = DecodeCliText(utf8);
        try
        {
            if (ReachedCliReadLimit(System.Text.Encoding.UTF8.GetBytes(text)))
            {
                return false;
            }

            using var document = JsonDocument.Parse(text);
            return !accepts(document.RootElement);
        }
        catch (JsonException)
        {
            // The scan stops explicitly before depth can throw; this is syntax before that limit.
            return true;
        }
        catch (InvalidOperationException)
        {
            // An undecodable property name gives the SDK no verdict about the CLI's object keys.
            return false;
        }
    }

    private static bool ReachedCliReadLimit(ReadOnlySpan<byte> utf8)
    {
        var reader = new Utf8JsonReader(utf8, new JsonReaderOptions { MaxDepth = int.MaxValue });
        while (reader.Read())
        {
            if (reader.CurrentDepth >= 64 && reader.TokenType is JsonTokenType.StartObject or JsonTokenType.StartArray)
            {
                return true;
            }
        }

        return false;
    }

    private static bool IsCliShape(JsonElement root, bool admitLegacy) =>
        root.ValueKind == JsonValueKind.Object && IsValidDisabled(root) && IsValidRemote(root, admitLegacy)
        && IsValidRemoteAccepted(root) && IsValidPort(root) && IsValidCors(root)
        && (!root.TryGetProperty("hostname", out var hostname) || hostname.ValueKind == JsonValueKind.String)
        && (!root.TryGetProperty("password", out var password) || password.ValueKind == JsonValueKind.String)
        && (!root.TryGetProperty("env", out var env) || (env.ValueKind == JsonValueKind.Object
            && env.EnumerateObject().All(entry => env.GetProperty(entry.Name).ValueKind == JsonValueKind.String)));

    /// <summary>Validates a config document and extracts its environment map.</summary>
    /// <param name="utf8">The file's bytes.</param>
    /// <returns>The environment map (empty when the document declares none), or null for an invalid document.</returns>
    public static IReadOnlyDictionary<string, string>? TryReadEnvironment(ReadOnlySpan<byte> utf8) =>
        StrictJson.TryRead(utf8, static root => Read(root, admitLegacyRemote: true));

    /// <summary>
    /// Whether a document decodes under the current shape alone, the gate of the CLI's
    /// <c>migrateConfig</c>: a legacy boolean <c>remote</c> does not.
    /// </summary>
    /// <param name="utf8">The file's bytes.</param>
    /// <returns>True when the document is a valid config in the current shape.</returns>
    public static bool IsCurrent(ReadOnlySpan<byte> utf8) =>
        StrictJson.TryRead(utf8, static root => Read(root, admitLegacyRemote: false)) is not null;

    /// <summary>Materializes every declared member in the current or legacy configuration shape.</summary>
    /// <param name="utf8">The file's bytes.</param>
    /// <returns>The decoded document, or null when its shape or a declared string cannot be decoded.</returns>
    public static ServiceConfigDocument? TryReadDocument(ReadOnlySpan<byte> utf8) =>
        StrictJson.TryRead(utf8, Materialize);

    private static ServiceConfigDocument? Materialize(JsonElement root)
    {
        var environment = Read(root, admitLegacyRemote: true);
        if (environment is null)
        {
            return null;
        }

        _ = StrictJson.TryGetOptionalString(root, "hostname", out var hostname);
        _ = StrictJson.TryGetOptionalString(root, "password", out var password);
        _ = root.TryGetProperty("remote", out var remote);
        return new ServiceConfigDocument
        {
            Disabled = root.TryGetProperty("disabled", out var disabled) ? disabled.GetBoolean() : null,
            RemoteRoute = remote.ValueKind == JsonValueKind.Object ? remote.GetProperty("route").GetString() : null,
            LegacyRemote = remote.ValueKind is JsonValueKind.True or JsonValueKind.False ? remote.GetBoolean() : null,
            RemoteAccepted = root.TryGetProperty("remoteAccepted", out _) ? true : null,
            Hostname = hostname,
            Port = root.TryGetProperty("port", out var port) ? (int)port.GetDouble() : null,
            Password = password,
            // The shape has proved these strings non-null; GetString also forces strict decoding.
            CorsOrigins = root.TryGetProperty("cors", out var cors)
                ? cors.EnumerateArray().Select(static origin => origin.GetString()!).ToArray() : null,
            Environment = root.TryGetProperty("env", out _) ? environment : null,
        };
    }

    private static Dictionary<string, string>? Read(JsonElement root, bool admitLegacyRemote)
    {
        // Read validates the password and route and discards them; Materialize alone keeps them.
        if (root.ValueKind != JsonValueKind.Object
            || !IsValidDisabled(root)
            || !IsValidRemote(root, admitLegacyRemote)
            || !IsValidRemoteAccepted(root)
            || !StrictJson.TryGetOptionalString(root, "hostname", out _)
            || !StrictJson.TryGetOptionalString(root, "password", out _)
            || !IsValidPort(root)
            || !IsValidCors(root))
        {
            return null;
        }

        var environment = new Dictionary<string, string>(StringComparer.Ordinal);
        if (!root.TryGetProperty("env", out var env))
        {
            return environment;
        }

        if (env.ValueKind != JsonValueKind.Object)
        {
            return null;
        }

        foreach (var entry in env.EnumerateObject())
        {
            if (entry.Value.ValueKind != JsonValueKind.String)
            {
                return null;
            }

            environment.Add(entry.Name, entry.Value.GetString()!);
        }

        return environment;
    }

    private static bool IsValidDisabled(JsonElement root) =>
        !root.TryGetProperty("disabled", out var disabled) ||
        disabled.ValueKind is JsonValueKind.True or JsonValueKind.False;

    /// <summary>
    /// <c>Struct({ route: String })</c>, whose decode ignores any other member of the object, or, for
    /// the legacy decode only, a boolean.
    /// </summary>
    private static bool IsValidRemote(JsonElement root, bool admitLegacy)
    {
        if (!root.TryGetProperty("remote", out var remote))
        {
            return true;
        }

        if (remote.ValueKind == JsonValueKind.Object)
        {
            return remote.TryGetProperty("route", out var route) && route.ValueKind == JsonValueKind.String;
        }

        return admitLegacy && (remote.ValueKind is JsonValueKind.True or JsonValueKind.False);
    }

    /// <summary>
    /// <c>Literal(true)</c>: the CLI records that remote access was accepted and writes nothing
    /// else there, so any other value fails the decode.
    /// </summary>
    private static bool IsValidRemoteAccepted(JsonElement root) =>
        !root.TryGetProperty("remoteAccepted", out var accepted) || accepted.ValueKind == JsonValueKind.True;

    private static bool IsValidPort(JsonElement root) =>
        !root.TryGetProperty("port", out var port) ||
        (StrictJson.TryGetSafeInteger(port, out var value) && value >= 1 && value <= MaxPort);

    private static bool IsValidCors(JsonElement root) =>
        !root.TryGetProperty("cors", out var cors) ||
        (cors.ValueKind == JsonValueKind.Array && cors.EnumerateArray().All(static origin => origin.ValueKind == JsonValueKind.String));
}
