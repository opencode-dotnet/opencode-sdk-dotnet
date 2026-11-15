using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>The background service's config as <c>opencode service get</c> shows it, without the password or remote route.</summary>
public sealed class OpenCodeServiceConfig
{
    internal OpenCodeServiceConfig(bool disabled, bool remoteEnabled, bool remoteAccepted, string? hostname,
        int? port, IReadOnlyList<string> corsOrigins, IReadOnlyDictionary<string, string> environment)
    {
        Disabled = disabled;
        RemoteEnabled = remoteEnabled;
        RemoteAccepted = remoteAccepted;
        Hostname = hostname;
        Port = port;
        CorsOrigins = corsOrigins;
        Environment = environment;
    }

    /// <summary>Gets the disabled preference, false when unset.</summary>
    /// <remarks>
    /// Only the CLI's connection-mode choice and pairing guard read this preference;
    /// <c>DiscoverAsync</c>, <c>EnsureAsync</c> and <c>StopAsync</c> do not.
    /// </remarks>
    public bool Disabled { get; }

    /// <summary>Gets whether remote access is on. Its private route is never exposed.</summary>
    public bool RemoteEnabled { get; }

    /// <summary>Gets whether remote access was accepted or was on before.</summary>
    public bool RemoteAccepted { get; }

    /// <summary>Gets the saved hostname, or null when unset.</summary>
    public string? Hostname { get; }

    /// <summary>Gets the saved port, or null when unset.</summary>
    public int? Port { get; }

    /// <summary>Gets the saved CORS origins in stored order, empty when unset.</summary>
    public IReadOnlyList<string> CorsOrigins { get; }

    /// <summary>Gets the saved environment entries, empty when unset.</summary>
    /// <remarks>A service start ignores a saved <c>XDG_STATE_HOME</c>; this read view keeps it.</remarks>
    public IReadOnlyDictionary<string, string> Environment { get; }

    internal static OpenCodeServiceConfig From(ServiceConfigDocument document) => new(
        document.Disabled == true,
        document.RemoteRoute is not null || document.LegacyRemote == true,
        document.RemoteAccepted == true,
        document.Hostname,
        document.Port,
        document.CorsOrigins ?? [],
        document.Environment ?? new Dictionary<string, string>(StringComparer.Ordinal));
}
