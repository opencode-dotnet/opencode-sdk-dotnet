using System.Buffers;
using System.Diagnostics;
using System.Runtime.CompilerServices;
using System.Text;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Reads one response body as a server-sent event stream and yields each dispatched frame.
/// Only <c>data</c> carries a payload and only <c>event</c> names the frame, so every other
/// field is skipped and a frame carrying no data never yields. One instance supports one enumeration at a time.
/// </summary>
internal sealed class ServerSentEventReader
{
    /// <summary>The pending-frame ceiling; a stream that never closes a frame cannot grow without bound.</summary>
    public const int DefaultMaxFrameCharacters = 16 * 1024 * 1024;

    private const int ReadBufferBytes = 8192;
    private const int InitialBufferBytes = 256;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);
    /// <summary>
    /// One conversion must cover the whole read so decoder diagnostics stay relative to its start. Declared after
    /// <see cref="StrictUtf8"/>, which its initializer reads.
    /// </summary>
    private static readonly int ScratchBufferCharacters = StrictUtf8.GetMaxCharCount(ReadBufferBytes);

    /// <summary>Stores the name after the data so both share one rental until the frame is dispatched.</summary>
    private byte[] _data = [];
    private byte[] _line = [];
    private readonly int _maxFrameCharacters;
    private readonly int _maxBufferBytes;
    private int _dataLength;
    private int _lineLength;
    private int _eventNameLength;
    private int _frameCharacters;
    private int _continuationBytes;
    private int _sequenceCharacters;
    private int _preambleLength;
    private bool _frameHasData;
    private bool _sawCarriageReturn;

    public ServerSentEventReader()
        : this(DefaultMaxFrameCharacters)
    {
    }

    public ServerSentEventReader(int maxFrameCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameCharacters, 1);

        _maxFrameCharacters = maxFrameCharacters;
        // A UTF-16 unit needs at most three UTF-8 bytes; an unfinished character adds at most three.
        _maxBufferBytes = (int)Math.Min(int.MaxValue, (3L * maxFrameCharacters) + 3);
    }

    public IAsyncEnumerable<ServerSentEvent> ReadAsync(Stream stream, CancellationToken cancellationToken)
    {
        // The guard runs on the call, not on the first enumeration.
        ArgumentNullException.ThrowIfNull(stream);

        return ReadCoreAsync(stream, cancellationToken);
    }

    private async IAsyncEnumerable<ServerSentEvent> ReadCoreAsync(Stream stream,
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        ResetEnumeration();
        var decoder = StrictUtf8.GetDecoder();
        var bytes = new byte[ReadBufferBytes];
        var scratch = ArrayPool<char>.Shared.Rent(ScratchBufferCharacters);

        void Validate(int byteCount, bool flush)
        {
            try
            {
                var offset = 0;
                bool completed;
                do
                {
                    decoder.Convert(bytes, offset, byteCount - offset, scratch, 0, scratch.Length, flush,
                        out var bytesUsed, out _, out completed);
                    offset += bytesUsed;
                }
                while (!completed);
            }
            catch (DecoderFallbackException exception)
            {
                throw new OpenCodeTransportException("The opencode event stream contains malformed UTF-8.", exception);
            }
        }

        try
        {
            EnsureCapacity(ref _line, required: 1, used: 0);
            EnsureCapacity(ref _data, required: 1, used: 0);
            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var read = await stream.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
                if (read is 0)
                {
                    break;
                }

                // Validate the whole arrival before dispatching anything from it, including ignored fields.
                Validate(read, flush: false);
                var position = 0;
                SkipPreamble(bytes, read, ref position);
                while (position < read)
                {
                    if (ScanBytes(bytes, read, ref position) is { } payload)
                    {
                        cancellationToken.ThrowIfCancellationRequested();
                        yield return payload;
                    }
                }
            }

            // Decoder failure precedes truncation when the body ends inside a UTF-8 character.
            Validate(byteCount: 0, flush: true);

            // A body may end without the blank line that closes its last frame, and that frame
            // is whole. A body ending mid-line was cut instead, and no connection resumes another's
            // bytes, so the truncation is reported rather than passed off as a complete event.
            if (_lineLength > 0)
            {
                throw new OpenCodeTransportException("The opencode event stream ended in the middle of an event.");
            }

            if (TakeFrame() is { } trailing)
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return trailing;
            }
        }
        finally
        {
            ReturnBuffer(ref _line);
            ReturnBuffer(ref _data);
            ArrayPool<char>.Shared.Return(scratch);
        }
    }

    private void SkipPreamble(byte[] bytes, int length, ref int position)
    {
        // One leading byte order mark belongs to the framing, not to the first field.
        var preamble = "\uFEFF"u8;
        while (_preambleLength < preamble.Length && position < length)
        {
            if (bytes[position] != preamble[_preambleLength])
            {
                AppendLine(preamble[.._preambleLength]);
                _preambleLength = preamble.Length;
                break;
            }

            _preambleLength++;
            position++;
        }
    }

    /// <summary>Scans bytes by line, returning when one frame is ready or the buffer is exhausted.</summary>
    private ServerSentEvent? ScanBytes(byte[] bytes, int length, ref int position)
    {
        while (position < length)
        {
            // A CR already terminated the line, so the LF completing a CRLF pair is not a second terminator.
            if (_sawCarriageReturn)
            {
                _sawCarriageReturn = false;
                if (bytes[position] is (byte)'\n')
                {
                    position++;
                    continue;
                }
            }

            // ASCII delimiters cannot occur inside a multi-byte UTF-8 character.
            var remaining = bytes.AsSpan(position, length - position);
            var terminator = remaining.IndexOfAny((byte)'\r', (byte)'\n');
            if (terminator < 0)
            {
                AppendLine(remaining);
                position = length;
                break;
            }

            if (terminator > 0)
            {
                AppendLine(remaining[..terminator]);
            }

            _sawCarriageReturn = bytes[position + terminator] is (byte)'\r';
            position += terminator + 1;
            if (CompleteLine() is { } frame)
            {
                return frame;
            }
        }

        return null;
    }

    private void AppendLine(ReadOnlySpan<byte> bytes)
    {
        foreach (var value in bytes)
        {
            if (value < 0x80)
            {
                Grow(1);
            }
            else if (value >= 0xc0)
            {
                _continuationBytes = value switch
                {
                    < 0xe0 => 1,
                    < 0xf0 => 2,
                    _ => 3,
                };
                _sequenceCharacters = value < 0xf0 ? 1 : 2;
            }
            else if (--_continuationBytes is 0)
            {
                Grow(_sequenceCharacters);
            }
        }

        EnsureCapacity(ref _line, _lineLength + bytes.Length, _lineLength);
        bytes.CopyTo(_line.AsSpan(_lineLength));
        _lineLength += bytes.Length;
    }

    /// <summary>Closes the pending line: an empty one ends the frame, a field line extends it.</summary>
    private ServerSentEvent? CompleteLine()
    {
        if (_lineLength is 0)
        {
            return TakeFrame();
        }

        // A line opening with the separator is a comment, which carries no field.
        if (_line[0] is not (byte)':')
        {
            var line = _line.AsSpan(0, _lineLength);
            var separator = line.IndexOf((byte)':');
            var name = separator < 0 ? line : line[..separator];
            var start = ValueStart(separator);
            if (name.SequenceEqual("data"u8))
            {
                AppendDataValue(line[start..]);
            }
            else if (name.SequenceEqual("event"u8))
            {
                CaptureEventName(line[start..]);
            }
        }

        _lineLength = 0;
        return null;
    }

    private ServerSentEvent? TakeFrame()
    {
        if (!_frameHasData)
        {
            ResetFrame();
            return null;
        }

        // An absent or empty event field leaves the frame unnamed, which the grammar reads
        // as the default name.
        var name = _eventNameLength is 0
            ? ServerSentEvent.DefaultName
            : StrictUtf8.GetString(_data, _dataLength, _eventNameLength);
        var frame = new ServerSentEvent(name, _data.AsMemory(0, _dataLength));
        ResetFrame();
        return frame;
    }

    private void EnsureCapacity(ref byte[] buffer, int required, int used)
    {
        // Valid input cannot reach beyond this bound: Grow refuses before the buffer passes it.
        Debug.Assert(required <= _maxBufferBytes, "Required capacity must stay within the charged-character bound.");
        if (required <= buffer.Length)
        {
            return;
        }

        var capacity = buffer.Length is 0 ? Math.Min(InitialBufferBytes, _maxBufferBytes) : buffer.Length;
        while (capacity < required)
        {
            capacity = (int)Math.Min(_maxBufferBytes, 2L * capacity);
        }

        var replacement = ArrayPool<byte>.Shared.Rent(capacity);
        buffer.AsSpan(0, used).CopyTo(replacement);
        ReturnBuffer(ref buffer);
        buffer = replacement;
    }

    private void AppendDataValue(ReadOnlySpan<byte> value)
    {
        var joinedLength = _dataLength + value.Length;
        if (_frameHasData)
        {
            Grow(1);
            joinedLength++;
        }

        EnsureCapacity(ref _data, joinedLength + _eventNameLength, _dataLength + _eventNameLength);
        _data.AsSpan(_dataLength, _eventNameLength).CopyTo(_data.AsSpan(joinedLength));
        if (_frameHasData)
        {
            _data[_dataLength++] = (byte)'\n';
        }

        value.CopyTo(_data.AsSpan(_dataLength));
        _dataLength = joinedLength;
        _frameHasData = true;
    }

    private void CaptureEventName(ReadOnlySpan<byte> value)
    {
        // The grammar names event: message the same as an unnamed frame, so skip decoding a string.
        _eventNameLength = value.SequenceEqual("message"u8) ? 0 : value.Length;
        if (_eventNameLength > 0)
        {
            EnsureCapacity(ref _data, _dataLength + _eventNameLength, _dataLength);
            value.CopyTo(_data.AsSpan(_dataLength));
        }
    }

    /// <summary>Locates a field value: past the separator, then past at most one space.</summary>
    private int ValueStart(int separator)
    {
        // A line with no separator is a field name whose value is empty.
        var start = separator < 0 ? _lineLength : separator + 1;
        return start < _lineLength && _line[start] is (byte)' ' ? start + 1 : start;
    }

    private void Grow(int characters)
    {
        _frameCharacters += characters;
        if (_frameCharacters > _maxFrameCharacters)
        {
            throw new OpenCodeTransportException(
                "The opencode event stream produced a frame beyond the size this client accepts.");
        }
    }

    private void ResetFrame()
    {
        _dataLength = 0;
        _eventNameLength = 0;
        _frameCharacters = 0;
        _frameHasData = false;
    }

    private void ResetEnumeration()
    {
        ResetFrame();
        _lineLength = 0;
        _continuationBytes = 0;
        _sequenceCharacters = 0;
        _preambleLength = 0;
        _sawCarriageReturn = false;
    }

    private static void ReturnBuffer(ref byte[] buffer)
    {
        if (buffer.Length > 0)
        {
            ArrayPool<byte>.Shared.Return(buffer);
            buffer = [];
        }
    }
}
