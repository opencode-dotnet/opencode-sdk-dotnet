using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// The terminal's process ended. Knowledge source: upstream-observed — unlike the normal PTY
/// family the exit code is on this wire, though the daemon reports none when the process was
/// signalled or the code is unknown. The server closes the socket normally right after, and a
/// terminal that exits while attached is removed by the server rather than lingering.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public sealed class PersistentPtyExitedFrame : PersistentPtyFrame
{
    /// <summary>
    /// Initializes an exited frame. Public so a consumer substituting
    /// <see cref="PersistentPtySession"/> can script the frames its override yields; the SDK's own
    /// decoder uses the same door.
    /// </summary>
    /// <param name="exitCode">The process exit code, or null when the daemon reported none.</param>
    /// <param name="finalOffset">The output cursor the terminal's output ended at.</param>
    public PersistentPtyExitedFrame(int? exitCode, long finalOffset)
    {
        ExitCode = exitCode;
        FinalOffset = finalOffset;
    }

    /// <summary>Gets the process exit code, or null when the daemon reported none.</summary>
    public int? ExitCode { get; }

    /// <summary>Gets the output cursor the terminal's output ended at.</summary>
    public long FinalOffset { get; }
}
