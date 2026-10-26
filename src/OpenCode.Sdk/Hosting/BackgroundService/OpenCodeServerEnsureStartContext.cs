namespace OpenCode.Sdk;

/// <summary>
/// Describes why ensuring the background service requires a new process. Passed to
/// <see cref="OpenCodeServerEnsureOptions.OnStart"/> before a new service process is spawned.
/// </summary>
public sealed class OpenCodeServerEnsureStartContext
{
    internal OpenCodeServerEnsureStartContext(OpenCodeServerEnsureReason reason, string? previousVersion)
    {
        Reason = reason;
        PreviousVersion = previousVersion;
    }

    /// <summary>Gets the reason a new service process is required.</summary>
    public OpenCodeServerEnsureReason Reason { get; }

    /// <summary>
    /// Gets the version the registered service reported when <see cref="Reason"/> is
    /// <see cref="OpenCodeServerEnsureReason.VersionMismatch"/> and it reported one; otherwise null.
    /// </summary>
    public string? PreviousVersion { get; }
}
