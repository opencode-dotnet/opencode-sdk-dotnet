using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// The terminal was resized — by this connection, by another controller, or by the HTTP update
/// door. Knowledge source: upstream-observed — the checkpoint is the terminal-escape byte stream
/// that repaints the screen at the new size, prefixed with <c>ESC c</c>; a caller driving an
/// emulator resizes it to the new viewport and writes those bytes. The session tracks the new
/// viewport itself, so later writes carry it without the caller repeating the resize.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public sealed class PersistentPtyResizedFrame : PersistentPtyFrame
{
    /// <summary>
    /// Initializes a resized frame. Public so a consumer substituting
    /// <see cref="PersistentPtySession"/> can script the frames its override yields; the SDK's own
    /// decoder uses the same door.
    /// </summary>
    /// <param name="cols">The new column count.</param>
    /// <param name="rows">The new row count.</param>
    /// <param name="generation">The resize generation this size belongs to.</param>
    /// <param name="checkpoint">The terminal-escape bytes that repaint the screen at the new size.</param>
    public PersistentPtyResizedFrame(int cols, int rows, long generation, ReadOnlyMemory<byte> checkpoint)
    {
        Cols = cols;
        Rows = rows;
        Generation = generation;
        Checkpoint = checkpoint;
    }

    /// <summary>Gets the new column count.</summary>
    public int Cols { get; }

    /// <summary>Gets the new row count.</summary>
    public int Rows { get; }

    /// <summary>Gets the resize generation this size belongs to; it advances on every resize.</summary>
    public long Generation { get; }

    /// <summary>Gets the terminal-escape bytes that repaint the screen at the new size.</summary>
    public ReadOnlyMemory<byte> Checkpoint { get; }
}
