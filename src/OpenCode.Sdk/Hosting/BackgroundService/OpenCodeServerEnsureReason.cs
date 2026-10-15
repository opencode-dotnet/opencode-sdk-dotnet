namespace OpenCode.Sdk;

/// <summary>
/// Why ensuring the background service requires a new process. Passed to
/// <see cref="OpenCodeServerEnsureOptions.OnStart"/> at most once per
/// <see cref="OpenCodeServer.EnsureAsync"/> call.
/// </summary>
public enum OpenCodeServerEnsureReason
{
    /// <summary>No usable service is registered, or the registered daemon is unresponsive.</summary>
    Missing = 0,

    /// <summary>A registered service is running at a version that does not match the expected one.</summary>
    VersionMismatch = 1,
}
