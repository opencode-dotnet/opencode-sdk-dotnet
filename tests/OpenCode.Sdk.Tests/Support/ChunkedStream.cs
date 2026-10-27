using System.Text;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Hands out one prepared chunk per read, so a test can place a frame boundary mid-chunk.</summary>
internal sealed class ChunkedStream : Stream
{
    private readonly Queue<byte[]> _chunks;
    private byte[]? _current;
    private int _offset;

    private ChunkedStream(IEnumerable<byte[]> chunks)
    {
        // An empty chunk would read as end-of-stream, cutting the body short.
        _chunks = new Queue<byte[]>(chunks.Where(static chunk => chunk.Length > 0));
    }

    /// <summary>Gets whether the stream was disposed, which is how a test observes cleanup.</summary>
    public bool Disposed { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public static ChunkedStream Of(params string[] chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        return new ChunkedStream(chunks.Select(static chunk => Encoding.UTF8.GetBytes(chunk)));
    }

    /// <summary>Hands out explicit byte chunks, including malformed or split UTF-8 sequences.</summary>
    public static ChunkedStream OfBytes(params byte[][] chunks)
    {
        ArgumentNullException.ThrowIfNull(chunks);

        return new ChunkedStream(chunks);
    }

    /// <summary>Splits one payload into fixed-size byte chunks, cutting multi-byte characters apart.</summary>
    public static ChunkedStream OfBytes(string payload, int chunkSize)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkSize, 1);

        var bytes = Encoding.UTF8.GetBytes(payload);
        var chunks = new List<byte[]>();
        for (var offset = 0; offset < bytes.Length; offset += chunkSize)
        {
            chunks.Add([.. bytes.Skip(offset).Take(chunkSize)]);
        }

        return new ChunkedStream(chunks);
    }

    public override int Read(byte[] buffer, int offset, int count)
    {
        ArgumentNullException.ThrowIfNull(buffer);
        if (_current is null && _chunks.Count is 0)
        {
            return 0;
        }

        // A chunk the caller could not take in one read stays at the head, so its remainder
        // is served next rather than jumping the queue behind later chunks.
        _current ??= _chunks.Dequeue();
        var length = Math.Min(_current.Length - _offset, count);
        Array.Copy(_current, _offset, buffer, offset, length);
        _offset += length;
        if (_offset == _current.Length)
        {
            _current = null;
            _offset = 0;
        }

        return length;
    }

    public override void Flush()
    {
    }

    protected override void Dispose(bool disposing)
    {
        Disposed = true;
        base.Dispose(disposing);
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();
}
