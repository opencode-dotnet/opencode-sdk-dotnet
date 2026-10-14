using System.Net.Http.Headers;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The construction-time connection facts a door outside the HTTP pipeline needs to address the
/// same server the pipeline addresses. The PTY WebSocket upgrade is that door: it builds its own
/// socket, so it cannot reach the endpoint the pipeline holds privately or the credential the
/// decoration policy holds internally. The snapshot is read once at construction exactly as the
/// policies' own snapshot is, so mutating the options object afterwards never changes a built
/// client.
/// </summary>
/// <remarks>
/// Deliberately a class, not a record: a record synthesizes a member-printing
/// <see cref="object.ToString"/>, which would render the <c>Authorization</c> header value. Basic
/// authentication is reversible base64 — that is the password in plaintext, one interpolation away
/// from a log line or an exception message. Nothing here needs value equality, so the type carries
/// no rendering to redact.
/// </remarks>
internal sealed class ConnectionSnapshot
{
    public ConnectionSnapshot(string endpointBase, AuthenticationHeaderValue? authorization, LocationSelector? location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(endpointBase);

        EndpointBase = endpointBase;
        Authorization = authorization;
        Location = location;
    }

    /// <summary>Gets the Basic credential every request carries, or null when no password was configured.</summary>
    public AuthenticationHeaderValue? Authorization { get; }

    /// <summary>Gets the normalized request base: scheme, authority, and path prefix without a trailing slash.</summary>
    public string EndpointBase { get; }

    /// <summary>Gets the ambient location a per-call selector merges over member by member.</summary>
    public LocationSelector? Location { get; }
}
