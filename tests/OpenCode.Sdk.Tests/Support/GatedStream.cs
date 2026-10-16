using System.Threading.Channels;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// A read-only stream that hands out a payload in chunks, each only when the test releases it,
/// and reports every read it is asked for, so a test decides when the exchange progresses. The read
/// after the last chunk ends the stream at once. A pending read honours its token.
/// </summary>
internal sealed class GatedStream : Stream
{
    private readonly byte[] _payload;
    private readonly int _chunkCount;
    private readonly int _chunkSize;
    private readonly Channel<int> _requested = Channel.CreateUnbounded<int>();
    private readonly Channel<bool> _released = Channel.CreateUnbounded<bool>();
    private int _position;
    private int _sent;

    public GatedStream(byte[] payload, int chunkCount)
    {
        ArgumentNullException.ThrowIfNull(payload);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkCount, 1);
        if (payload.Length < chunkCount)
        {
            throw new ArgumentException("Every chunk carries at least one byte.", nameof(payload));
        }

        _payload = payload;
        _chunkCount = chunkCount;
        _chunkSize = payload.Length / chunkCount;
    }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    /// <summary>Completes once the reader asks for the next chunk, with the room that read offers.</summary>
    /// <param name="hangGuard">Bounds a reader that never asks; it decides nothing else.</param>
    /// <returns>A task that completes with the pending read's buffer length.</returns>
    public Task<int> WaitForReadAsync(TimeSpan hangGuard) => _requested.Reader.ReadAsync().AsTask().WaitAsync(hangGuard);

    /// <summary>Lets the pending read, or the next one, return its chunk.</summary>
    public void Release() => _ = _released.Writer.TryWrite(true);

    /// <summary>
    /// Hands out every chunk: for each, waits for the read that asks for it, runs
    /// <paramref name="beforeChunk"/>, and releases it.
    /// </summary>
    /// <param name="beforeChunk">What happens while the read waits, such as moving a clock.</param>
    /// <param name="hangGuard">Bounds each wait for a read; it decides nothing else.</param>
    /// <returns>A task that completes once the last chunk is released.</returns>
    public async Task DeliverAsync(Action beforeChunk, TimeSpan hangGuard)
    {
        ArgumentNullException.ThrowIfNull(beforeChunk);

        for (var chunk = 0; chunk < _chunkCount; chunk++)
        {
            await WaitForReadAsync(hangGuard);
            beforeChunk();
            Release();
        }
    }

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadChunkAsync(buffer.AsMemory(offset, count), cancellationToken);

#if NET
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        new(ReadChunkAsync(buffer, cancellationToken));
#endif

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("The gated stream is read asynchronously only.");

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private async Task<int> ReadChunkAsync(Memory<byte> buffer, CancellationToken cancellationToken)
    {
        if (_sent == _chunkCount)
        {
            return 0;
        }

        _ = _requested.Writer.TryWrite(buffer.Length);
        _ = await _released.Reader.ReadAsync(cancellationToken);

        // The last chunk carries the remainder, so the whole payload goes out in exactly the
        // chunks asked for.
        var size = _sent == _chunkCount - 1 ? _payload.Length - _position : _chunkSize;
        if (buffer.Length < size)
        {
            throw new InvalidOperationException("The reader asked for less than one chunk; every chunk is one read.");
        }

        _payload.AsSpan(_position, size).CopyTo(buffer.Span);
        _position += size;
        _sent++;
        return size;
    }
}
