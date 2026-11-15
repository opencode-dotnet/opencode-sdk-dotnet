namespace OpenCode.Sdk;

/// <summary>The explicit server's version and the pinned release it differs from.</summary>
public sealed class OpenCodeServerVersionMismatchContext
{
    internal OpenCodeServerVersionMismatchContext(Uri endpoint, string serverVersion, string clientVersion)
    {
        Endpoint = endpoint;
        ServerVersion = serverVersion;
        ClientVersion = clientVersion;
    }

    /// <summary>Gets the explicitly selected server's endpoint.</summary>
    public Uri Endpoint { get; }

    /// <summary>Gets the version the explicit probe reported.</summary>
    public string ServerVersion { get; }

    /// <summary>Gets the SDK's pinned release comparand, not the version of a CLI found on PATH.</summary>
    public string ClientVersion { get; }
}
