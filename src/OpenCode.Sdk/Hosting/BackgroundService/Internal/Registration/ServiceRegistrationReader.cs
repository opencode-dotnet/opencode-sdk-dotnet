using System.Net;
using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The strict hand-written boundary over the registration file (<c>Service.Info</c> at the pin:
/// optional <c>id</c> and <c>version</c> strings, required <c>url</c>, required positive
/// <c>pid</c>, optional <c>password</c>). No serializer context and no type that could print the
/// password. Every invalid document is absent state, the way upstream's <c>read</c> folds a decode
/// failure into "no registration"; the absolute-URL, Int32-pid, and duplicate-member rules are the
/// SDK's own, stricter than the JSON.parse upstream decodes with.
/// </summary>
internal static class ServiceRegistrationReader
{
    /// <summary>Reads and decodes the registration at the path; a file with nothing usable in it is absent state.</summary>
    /// <param name="fileSystem">The file access.</param>
    /// <param name="path">The absolute path.</param>
    /// <param name="cancellationToken">The caller's token; cancellation is the one failure that propagates.</param>
    /// <returns>The registration, or null for anything the daemon could not have written.</returns>
    public static async Task<ServiceRegistration?> TryReadAsync(IServiceFileSystem fileSystem, string path, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        var bytes = await fileSystem.TryReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
        return bytes is null ? null : TryRead(bytes);
    }

    /// <summary>Decodes a registration document.</summary>
    /// <param name="utf8">The file's bytes.</param>
    /// <returns>The registration, or null for any document the daemon could not have written.</returns>
    public static ServiceRegistration? TryRead(ReadOnlySpan<byte> utf8) => StrictJson.TryRead(utf8, Read);

    /// <summary>Whether the pinned client's probe can use the URL as the base of its info-route address.</summary>
    /// <param name="url">The caller's URL.</param>
    /// <returns>False for a URL the probe could not resolve against.</returns>
    public static bool CanBeBase(Uri url)
    {
        if (!url.IsAbsoluteUri)
        {
            return false;
        }

        var text = url.OriginalString;
        var start = 0;
        var end = text.Length;
        while (start < end && text[start] <= ' ')
        {
            start++;
        }

        // Mirror WHATWG's trailing C0-control-and-space strip.
        while (end > start && text[end - 1] <= ' ')
        {
            end--;
        }

        text = new string([.. text[start..end].Where(static value => value is not '\t' and not '\n' and not '\r')]);
        var colon = text.IndexOf(':', StringComparison.Ordinal);
        if (colon < 0)
        {
            return false;
        }

        var scheme = text[..colon];
        if (!Uri.CheckSchemeName(scheme))
        {
            return false;
        }

        return string.Equals(scheme, "http", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, "https", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, "ws", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, "wss", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, "ftp", StringComparison.OrdinalIgnoreCase)
            || string.Equals(scheme, "file", StringComparison.OrdinalIgnoreCase)
            || (colon + 1 < text.Length && text[colon + 1] == '/');
    }

    /// <summary>The registration at another address, preserving its credential and identity.</summary>
    /// <param name="registration">The registration that supplies the identity and credential.</param>
    /// <param name="url">An address accepted by <see cref="CanBeBase"/>; the reader's HTTP or HTTPS URL rules apply.</param>
    /// <returns>
    /// The registration with the URL string kept verbatim for identity and an unspecified address
    /// connected as the same family's loopback; null for a URL the daemon could not have registered.
    /// </returns>
    public static ServiceRegistration? AtUrl(ServiceRegistration registration, Uri url) =>
        IsHttp(url)
            ? new ServiceRegistration(registration.Id, registration.Version, url.OriginalString, ConnectTarget(url), registration.ProcessId, registration.Password)
            : null;

    private static ServiceRegistration? Read(JsonElement root)
    {
        if (root.ValueKind != JsonValueKind.Object
            || !StrictJson.TryGetOptionalString(root, "id", out var id)
            || !StrictJson.TryGetOptionalString(root, "version", out var version)
            || !StrictJson.TryGetOptionalString(root, "password", out var password)
            || !StrictJson.TryGetOptionalString(root, "url", out var url)
            || url is null
            || !root.TryGetProperty("pid", out var pid)
            || !StrictJson.TryGetProcessId(pid, out var processId)
            || processId <= 0
            || !Uri.TryCreate(url, UriKind.Absolute, out var endpoint)
            || !IsHttp(endpoint))
        {
            return null;
        }

        // Only a missing member means no credential, as in the pinned client: the daemon serves
        // with config.password || random, so a whitespace password is one it really runs with.
        return new ServiceRegistration(id, version, url, ConnectTarget(endpoint), processId, password);
    }

    private static bool IsHttp(Uri endpoint) =>
        string.Equals(endpoint.Scheme, Uri.UriSchemeHttp, StringComparison.Ordinal) ||
        string.Equals(endpoint.Scheme, Uri.UriSchemeHttps, StringComparison.Ordinal);

    /// <summary>
    /// A daemon bound to every interface registers the unspecified address (<c>0.0.0.0</c> or
    /// <c>::</c>), which Bun connects to as loopback (measured on Windows and Linux, libuv's rule)
    /// and .NET refuses as a target. The connect target maps it to the same family's loopback; the
    /// raw <see cref="ServiceRegistration.Url"/> keeps what the daemon wrote for identity.
    /// </summary>
    private static Uri ConnectTarget(Uri endpoint)
    {
        if (endpoint.HostNameType is not (UriHostNameType.IPv4 or UriHostNameType.IPv6)
            || !IPAddress.TryParse(endpoint.DnsSafeHost, out var address))
        {
            return endpoint;
        }

        if (address.Equals(IPAddress.Any))
        {
            return new UriBuilder(endpoint) { Host = "127.0.0.1" }.Uri;
        }

        return address.Equals(IPAddress.IPv6Any) ? new UriBuilder(endpoint) { Host = "[::1]" }.Uri : endpoint;
    }
}
