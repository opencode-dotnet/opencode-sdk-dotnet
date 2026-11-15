namespace OpenCode.Sdk.Internal;

/// <summary>
/// The decoded service configuration, including the private password and remote route. A class,
/// not a record, so the default string representation never prints its members. Null means unset;
/// <see cref="LegacyRemote"/> also distinguishes the earlier boolean remote shape.
/// </summary>
internal sealed class ServiceConfigDocument
{
    /// <summary>Gets the saved connection-mode preference, or null when unset.</summary>
    public bool? Disabled { get; init; }

    /// <summary>Gets the current remote route, retained only inside the SDK.</summary>
    public string? RemoteRoute { get; init; }

    /// <summary>Gets the earlier boolean remote setting, or null for a current-shape document.</summary>
    public bool? LegacyRemote { get; init; }

    /// <summary>Gets the acceptance marker, true when present and null otherwise.</summary>
    public bool? RemoteAccepted { get; init; }

    /// <summary>Gets the saved hostname, or null when unset.</summary>
    public string? Hostname { get; init; }

    /// <summary>Gets the saved port, or null when unset.</summary>
    public int? Port { get; init; }

    /// <summary>Gets the stored password, retained only inside the SDK.</summary>
    public string? Password { get; init; }

    /// <summary>Gets the saved origins in stored order, or null when unset.</summary>
    public IReadOnlyList<string>? CorsOrigins { get; init; }

    /// <summary>Gets the saved environment entries, or null when unset.</summary>
    public IReadOnlyDictionary<string, string>? Environment { get; init; }
}
