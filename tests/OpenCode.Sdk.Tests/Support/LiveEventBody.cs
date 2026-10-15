using System.Text;
using System.Threading.Channels;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// An event-stream body the test writes while the client reads it, as a live server does: it
/// serves what was written, waits for more, and ends or fails only when the test says so. A pending
/// read honours its token, and the body reports when the client disposed it.
/// </summary>
internal sealed class LiveEventBody : Stream
{
    private readonly Channel<byte[]> _chunks = Channel.CreateUnbounded<byte[]>();
    private byte[] _current = [];
    private int _offset;
    private int _disposed;

    /// <summary>Creates a body that already holds the given text, ready to be read.</summary>
    /// <param name="written">The text the reader finds first.</param>
    /// <param name="ends">Ends the body once that text is read, as a server closing the stream does.</param>
    public LiveEventBody(string written = "", bool ends = false)
    {
        Append(written);
        if (ends)
        {
            End();
        }
    }

    public bool IsDisposed => Volatile.Read(ref _disposed) is not 0;

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Makes text available to the reader; empty text writes nothing.</summary>
    public void Append(string text)
    {
        ArgumentNullException.ThrowIfNull(text);

        if (text.Length > 0)
        {
            _ = _chunks.Writer.TryWrite(Encoding.UTF8.GetBytes(text));
        }
    }

    /// <summary>Ends the body cleanly once what was written is read, as a server closing the stream does.</summary>
    public void End() => _chunks.Writer.TryComplete();

    /// <summary>Fails the next read once what was written is read, as a dropped connection does.</summary>
    public void Fail() => _chunks.Writer.TryComplete(new IOException("Unable to read data from the transport connection."));

    /// <summary>The client reads a live body asynchronously only.</summary>
    public override int Read(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadCoreAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

#if NET
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        ReadCoreAsync(buffer, cancellationToken);
#endif

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        _ = Interlocked.Exchange(ref _disposed, 1);
        base.Dispose(disposing);
    }

    private async ValueTask<int> ReadCoreAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_offset == _current.Length)
        {
            if (!await _chunks.Reader.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
            {
                return 0;
            }

            _current = await _chunks.Reader.ReadAsync(cancellationToken).ConfigureAwait(false);
            _offset = 0;
        }

        var length = Math.Min(_current.Length - _offset, buffer.Length);
        _current.AsSpan(_offset, length).CopyTo(buffer.Span);
        _offset += length;
        return length;
    }
}
