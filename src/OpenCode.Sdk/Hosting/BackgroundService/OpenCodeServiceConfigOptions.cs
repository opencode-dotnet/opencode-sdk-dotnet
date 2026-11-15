namespace OpenCode.Sdk;

/// <summary>
/// Shapes a service-configuration operation. Every member is optional; null means the default,
/// and a blank value is refused. Values are read once when the call starts.
/// </summary>
public sealed class OpenCodeServiceConfigOptions
{
    /// <summary>
    /// Gets or sets the service channel whose config to read or change. Null selects the shared
    /// release config (<c>service.json</c>) used by the installed <c>opencode</c> command. A named
    /// channel follows the CLI's filename and legacy-donor rules.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>
    /// Gets or sets the CLI version whose legacy registration the stop an operation runs may
    /// migrate, the SDK's stand-in for the installed CLI's compiled version. The config itself
    /// migrates without a version check, as the CLI's does.
    /// </summary>
    public string? InstalledVersion { get; set; }
}
