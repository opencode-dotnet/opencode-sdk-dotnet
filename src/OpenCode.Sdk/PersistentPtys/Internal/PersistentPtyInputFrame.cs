using System.Buffers.Binary;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Encodes the framed input protocol (<c>input_protocol=1</c>):
/// <c>[type u8][cols u16 BE][rows u16 BE][data]</c>. Knowledge source: upstream-observed — the
/// server ignores frames shorter than five bytes and frames whose cols or rows are zero, so both
/// are refused here rather than sent to be dropped silently.
/// </summary>
internal static class PersistentPtyInputFrame
{
    /// <summary>The frame type carrying a viewport change and no data.</summary>
    public const byte ControlType = 0;

    /// <summary>The frame type carrying terminal input.</summary>
    public const byte InputType = 1;

    private const int HeaderLength = 5;

    /// <summary>Encodes one input frame.</summary>
    /// <param name="type">The frame type: <see cref="ControlType"/> or <see cref="InputType"/>.</param>
    /// <param name="cols">The viewport's column count; 1 through 65535.</param>
    /// <param name="rows">The viewport's row count; 1 through 65535.</param>
    /// <param name="data">The input bytes; empty for a control frame.</param>
    /// <returns>The bytes to send as one binary message.</returns>
    public static byte[] Encode(byte type, long cols, long rows, ReadOnlySpan<byte> data)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cols, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, ushort.MaxValue);

        var frame = new byte[HeaderLength + data.Length];
        frame[0] = type;
        WriteViewport(frame, cols, rows);
        data.CopyTo(frame.AsSpan(HeaderLength));
        return frame;
    }

    /// <summary>Writes a validated viewport into an already owned frame under send serialization.</summary>
    /// <param name="frame">The complete frame buffer, including its five-byte header.</param>
    /// <param name="cols">The viewport's column count.</param>
    /// <param name="rows">The viewport's row count.</param>
    public static void WriteViewport(byte[] frame, long cols, long rows)
    {
        ArgumentNullException.ThrowIfNull(frame);
        ArgumentOutOfRangeException.ThrowIfLessThan(cols, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(cols, ushort.MaxValue);
        ArgumentOutOfRangeException.ThrowIfLessThan(rows, 1);
        ArgumentOutOfRangeException.ThrowIfGreaterThan(rows, ushort.MaxValue);

        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1, 2), (ushort)cols);
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3, 2), (ushort)rows);
    }
}
