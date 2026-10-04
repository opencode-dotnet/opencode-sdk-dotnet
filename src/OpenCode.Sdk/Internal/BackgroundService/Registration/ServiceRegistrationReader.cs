using System.Net;
using System.Text.Json;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;

namespace OpenCode.Sdk.Internal.BackgroundService.Registration;

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
