using System.Diagnostics;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Reassembles one fragmented PTY WebSocket message. A replay chunk can arrive in several
/// fragments, and the decoder must see the whole message: a multi-byte UTF-8 sequence — or the
/// control frame's JSON body — can straddle a fragment boundary. One assembler serves a whole
/// session, growing to the largest message it has seen and reusing that buffer afterwards.
/// </summary>
internal sealed class PtyMessageAssembler
{
    private byte[] _buffer = [];

    /// <summary>
    /// Gets the buffer holding the fragments appended so far. It is owned by the assembler and is
    /// only valid until the next <see cref="Append"/> or <see cref="Reset"/>.
    /// </summary>
    public byte[] Buffer => _buffer;

    /// <summary>Gets how many bytes of <see cref="Buffer"/> the message occupies.</summary>
    public int Length { get; private set; }

    /// <summary>Appends one received fragment.</summary>
    /// <param name="fragment">The buffer the fragment was received into.</param>
    /// <param name="count">How many bytes of the fragment buffer are part of the message.</param>
    public void Append(byte[] fragment, int count)
    {
        Debug.Assert(count >= 0 && count <= fragment.Length, "The reported count never exceeds the receive buffer.");

        if (_buffer.Length - Length < count)
        {
            // Doubling keeps a many-fragment message from copying on every append.
            var capacity = Math.Max(_buffer.Length is 0 ? count : _buffer.Length * 2, Length + count);
            Array.Resize(ref _buffer, capacity);
        }

        Array.Copy(fragment, 0, _buffer, Length, count);
        Length += count;
    }

    /// <summary>Drops the assembled message, keeping the buffer for the next one.</summary>
    public void Reset() => Length = 0;
}
