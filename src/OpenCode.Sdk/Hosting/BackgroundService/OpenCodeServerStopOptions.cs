namespace OpenCode.Sdk;

/// <summary>
/// Shapes one <c>OpenCodeServer.StopAsync</c> call. Every member is optional; null means the
/// default, and a blank value is refused rather than treated as unset. The values are read once,
/// when the call starts.
/// </summary>
public sealed class OpenCodeServerStopOptions
{
    /// <summary>
    /// Gets or sets the service channel whose registration to stop. Null reads the shared release
    /// registration (<c>service.json</c>) that the installed <c>opencode</c> command publishes; a
    /// named channel (<c>local</c>, <c>dev</c>, a custom name) follows the CLI's filename and
    /// legacy-migration rules for that channel. Cannot be combined with
    /// <see cref="RegistrationFilePath"/>.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>
    /// Gets or sets an absolute path to a registration file to stop directly, bypassing channel
    /// resolution, the environment roots, and legacy migration. Cannot be combined with
    /// <see cref="Channel"/> or <see cref="InstalledVersion"/>.
    /// </summary>
    public string? RegistrationFilePath { get; set; }

    /// <summary>
    /// Gets or sets the CLI version whose legacy registration may be migrated to the current
    /// filename before it is read, the SDK's stand-in for the version compiled into the installed
    /// CLI. Only legacy migration reads it; a stop never compares versions otherwise.
    /// </summary>
    public string? InstalledVersion { get; set; }
}
