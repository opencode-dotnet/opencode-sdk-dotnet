using System.Runtime.CompilerServices;
using System.Text;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Reads one response body as a server-sent event stream and yields each dispatched frame.
/// Only <c>data</c> carries a payload and only <c>event</c> names the frame, so every other
/// field is skipped and a frame carrying no data never yields. One instance reads one body.
/// </summary>
internal sealed class ServerSentEventReader
{
    /// <summary>The pending-frame ceiling; a stream that never closes a frame cannot grow without bound.</summary>
    public const int DefaultMaxFrameCharacters = 16 * 1024 * 1024;

    private const string DataField = "data";
    private const string EventField = "event";
    private const char FieldSeparator = ':';
    private const char ByteOrderMark = '\uFEFF';
    private const int ReadBufferBytes = 8192;
    private static readonly Encoding StrictUtf8 = new UTF8Encoding(encoderShouldEmitUTF8Identifier: false, throwOnInvalidBytes: true);

    private readonly StringBuilder _data = new();
    private readonly StringBuilder _line = new();
    private readonly int _maxFrameCharacters;
#if !NET
    private char[] _copyBuffer = [];
#endif
    private string? _eventName;
    private int _frameCharacters;
    private bool _frameHasData;
    private bool _sawCarriageReturn;
    private bool _sawAnyCharacter;

    public ServerSentEventReader()
        : this(DefaultMaxFrameCharacters)
    {
    }

    public ServerSentEventReader(int maxFrameCharacters)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(maxFrameCharacters, 1);

        _maxFrameCharacters = maxFrameCharacters;
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
        // The decoder carries a partial multi-byte character across reads, so a chunk
        // boundary inside one character never corrupts it.
        var decoder = StrictUtf8.GetDecoder();
        var bytes = new byte[ReadBufferBytes];
        var characters = new char[Encoding.UTF8.GetMaxCharCount(ReadBufferBytes)];
#if !NET
        _copyBuffer = new char[characters.Length];
#endif

        int Decode(int byteCount, bool flush)
        {
            try
            {
                return decoder.GetChars(bytes, 0, byteCount, characters, 0, flush);
            }
            catch (DecoderFallbackException exception)
            {
                throw new OpenCodeTransportException("The opencode event stream contains malformed UTF-8.", exception);
            }
        }

        while (true)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var read = await stream.ReadAsync(bytes.AsMemory(), cancellationToken).ConfigureAwait(false);
            if (read is 0)
            {
                break;
            }

            var decoded = Decode(read, flush: false);
            var position = 0;
            while (position < decoded)
            {
                if (ScanDecoded(characters, decoded, ref position) is { } payload)
                {
                    cancellationToken.ThrowIfCancellationRequested();
                    yield return payload;
                }
            }
        }

        // Flushing makes the strict decoder reject a body cut inside a multi-byte character.
        _ = Decode(byteCount: 0, flush: true);

        // A body may end without the blank line that closes its last frame, and that frame
        // is whole. A body ending mid-line was cut instead, and this client never reconnects
        // to recover the remainder, so the truncation is reported rather than passed off as
        // a complete event.
        if (_line.Length > 0)
        {
            throw new OpenCodeTransportException("The opencode event stream ended in the middle of an event.");
        }

        if (TakeFrame() is { } trailing)
        {
            cancellationToken.ThrowIfCancellationRequested();
            yield return trailing;
        }
    }

    /// <summary>Scans decoded characters by line, returning when one frame is ready or the buffer is exhausted.</summary>
    private ServerSentEvent? ScanDecoded(char[] characters, int length, ref int position)
    {
        if (!_sawAnyCharacter && position < length)
        {
            _sawAnyCharacter = true;

            // One leading byte order mark belongs to the framing, not to the first field.
            if (characters[position] is ByteOrderMark)
            {
                position++;
            }
        }

        while (position < length)
        {
            // A CR already terminated the line, so the LF completing a CRLF pair is not a second terminator.
            if (_sawCarriageReturn)
            {
                _sawCarriageReturn = false;
                if (characters[position] is '\n')
                {
                    position++;
                    continue;
                }
            }

            var remaining = characters.AsSpan(position, length - position);
            var terminator = remaining.IndexOfAny('\r', '\n');
            if (terminator < 0)
            {
                AppendLine(characters, position, remaining.Length);
                position = length;
                break;
            }

            if (terminator > 0)
            {
                AppendLine(characters, position, terminator);
            }

            _sawCarriageReturn = characters[position + terminator] is '\r';
            position += terminator + 1;
            if (CompleteLine() is { } frame)
            {
                return frame;
            }
        }

        return null;
    }

    private void AppendLine(char[] characters, int start, int count)
    {
        Grow(count);
        _ = _line.Append(characters, start, count);
    }

    /// <summary>Closes the pending line: an empty one ends the frame, a field line extends it.</summary>
    private ServerSentEvent? CompleteLine()
    {
        if (_line.Length is 0)
        {
            return TakeFrame();
        }

        // A line opening with the separator is a comment, which carries no field.
        if (_line[0] is not FieldSeparator)
        {
            var separator = IndexOfSeparator();
            var nameLength = separator < 0 ? _line.Length : separator;
            if (MatchesField(DataField, nameLength))
            {
                AppendDataValue(separator);
            }
            else if (MatchesField(EventField, nameLength))
            {
                CaptureEventName(separator);
            }
        }

        _ = _line.Clear();
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
        var name = _eventName is { Length: > 0 } eventName ? eventName : ServerSentEvent.DefaultName;
        var frame = new ServerSentEvent(name, _data.ToString());
        ResetFrame();
        return frame;
    }

    private int IndexOfSeparator()
    {
        for (var index = 0; index < _line.Length; index++)
        {
            if (_line[index] is FieldSeparator)
            {
                return index;
            }
        }

        return -1;
    }

    private bool MatchesField(string field, int nameLength)
    {
        if (nameLength != field.Length)
        {
            return false;
        }

        for (var index = 0; index < nameLength; index++)
        {
            if (_line[index] != field[index])
            {
                return false;
            }
        }

        return true;
    }

    private void AppendDataValue(int separator)
    {
        // Every data field after the first contributes its own line to the payload.
        if (_frameHasData)
        {
            Grow(1);
            _ = _data.Append('\n');
        }

        _frameHasData = true;
        var start = ValueStart(separator);
#if NET
        _ = _data.Append(_line, start, _line.Length - start);
#else
        AppendDataValueDownlevel(start);
#endif
    }

#if !NET
    private void AppendDataValueDownlevel(int start)
    {
        var remaining = _line.Length - start;
        while (remaining > 0)
        {
            var count = Math.Min(remaining, _copyBuffer.Length);
            _line.CopyTo(start, _copyBuffer, 0, count);
            _ = _data.Append(_copyBuffer, 0, count);
            start += count;
            remaining -= count;
        }
    }
#endif

    private void CaptureEventName(int separator)
    {
        var start = ValueStart(separator);
        _eventName = _line.ToString(start, _line.Length - start);
    }

    /// <summary>Locates a field value: past the separator, then past at most one space.</summary>
    private int ValueStart(int separator)
    {
        // A line with no separator is a field name whose value is empty.
        var start = separator < 0 ? _line.Length : separator + 1;
        return start < _line.Length && _line[start] is ' ' ? start + 1 : start;
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
        _ = _data.Clear();
        _eventName = null;
        _frameCharacters = 0;
        _frameHasData = false;
    }
}
