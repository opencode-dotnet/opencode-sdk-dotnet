using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// The replay boundary: everything before it was retained output, everything after it is live.
/// Knowledge source: upstream-observed — the server sends it once, after the replay, even when
/// the replay was empty, carrying the output cursor the replay ended at. Storing that cursor is
/// how a later connection resumes exactly here through
/// <see cref="PersistentPtyConnectOptions.Cursor"/>.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public sealed class PersistentPtyReplayCompleteFrame : PersistentPtyFrame
{
    /// <summary>
    /// Initializes a replay-complete frame. Public so a consumer substituting
    /// <see cref="PersistentPtySession"/> can script the frames its override yields; the SDK's own
    /// decoder uses the same door.
    /// </summary>
    /// <param name="endOffset">The output cursor the replay ended at.</param>
    public PersistentPtyReplayCompleteFrame(long endOffset) => EndOffset = endOffset;

    /// <summary>Gets the output cursor the replay ended at.</summary>
    public long EndOffset { get; }
}
