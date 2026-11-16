namespace OpenCode.Sdk;

/// <summary>
/// Shapes a service-configuration operation. Channel and version are optional; blank values are
/// refused. Remote-on also takes a CLI command and environment. Values are captured when the call starts.
/// </summary>
public sealed class OpenCodeServiceConfigOptions
{
    /// <summary>
    /// Gets or sets the service channel whose config to read or change. Null selects the shared
    /// release config (<c>service.json</c>) used by the installed <c>opencode</c> command. A named
    /// channel follows the CLI's filename and legacy-donor rules.
    /// </summary>
    public string? Channel { get; set; }

    /// <summary>Gets or sets the CLI executable and its leading arguments; only remote-on reads this option.</summary>
    /// <remarks>
    /// Defaults to <c>opencode</c>, resolved as the standalone launcher's command.
    /// Remote-on appends <c>service set remote true</c>. The CLI writes its own compiled channel's
    /// config under its environment's roots, so this command, Channel and Environment must agree
    /// with the config the other operations read and remote-on reads its route from.
    /// </remarks>
    public IReadOnlyList<string> CliCommand { get; set; } = ["opencode"];

    /// <summary>Gets or sets entries layered over the process environment for the CLI; only remote-on reads this option.</summary>
    /// <remarks>
    /// Null adds no entries. This overlay reaches only the CLI, not the SDK's config read.
    /// The CLI writes its own compiled channel's config under these roots: CliCommand, Channel
    /// and Environment must agree with the config the other operations read and remote-on reads back.
    /// </remarks>
    public IReadOnlyDictionary<string, string>? Environment { get; set; }

    /// <summary>
    /// Gets or sets the CLI version whose legacy registration the stop an operation runs may
    /// migrate, the SDK's stand-in for the installed CLI's compiled version. The config itself
    /// migrates without a version check, as the CLI's does.
    /// </summary>
    public string? InstalledVersion { get; set; }
}
