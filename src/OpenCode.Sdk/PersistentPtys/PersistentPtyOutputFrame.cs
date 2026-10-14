using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>
/// Terminal output, exactly as the terminal produced it. Knowledge source: upstream-observed —
/// this family sends output on binary messages, so the bytes are never decoded: they carry
/// escape sequences and can split a multi-byte character across two frames. A caller feeding a
/// terminal emulator writes them as they are; a caller that wants text decodes the concatenated
/// stream itself, with replacement.
/// </summary>
[Experimental(ExperimentalDiagnostics.PersistentPty, UrlFormat = ExperimentalDiagnostics.PersistentPtyUrl)]
public sealed class PersistentPtyOutputFrame : PersistentPtyFrame
{
    /// <summary>
    /// Initializes an output frame. Public so a consumer substituting
    /// <see cref="PersistentPtySession"/> can script the frames its override yields; the SDK's own
    /// decoder uses the same door.
    /// </summary>
    /// <param name="data">The output bytes the frame carries.</param>
    public PersistentPtyOutputFrame(ReadOnlyMemory<byte> data) => Data = data;

    /// <summary>Gets the raw output bytes this frame carries.</summary>
    public ReadOnlyMemory<byte> Data { get; }
}
