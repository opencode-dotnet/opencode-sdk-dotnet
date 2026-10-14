using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// The terminal's foreground process changed. Knowledge source: upstream-observed — the daemon
/// reports the name of the process holding the terminal's foreground group, and null when the
/// shell itself is back in the foreground, which is how a caller tells "still running" from
/// "back at the prompt".
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public sealed class PersistentPtyForegroundProcessChangedFrame : PersistentPtyFrame
{
    /// <summary>
    /// Initializes a foreground-process-changed frame. Public so a consumer substituting
    /// <see cref="PersistentPtySession"/> can script the frames its override yields; the SDK's own
    /// decoder uses the same door.
    /// </summary>
    /// <param name="process">The foreground process name, or null when the shell is in the foreground.</param>
    public PersistentPtyForegroundProcessChangedFrame(string? process) => Process = process;

    /// <summary>Gets the foreground process name, or null when the shell itself is in the foreground.</summary>
    public string? Process { get; }
}
