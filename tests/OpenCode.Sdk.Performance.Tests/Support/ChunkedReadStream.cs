#if NET
using System.Threading.Tasks.Sources;
#endif

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// Serves a buffered body in reads no larger than one socket-sized chunk, so the reader's
/// decoder carry-over and cross-read frame assembly are measured instead of the 8 KiB whole
/// reads a <see cref="MemoryStream"/> delivers. By default every read completes synchronously, so
/// a rung over it measures CPU work alone. A suspending stream completes each asynchronous read on
/// the thread pool instead, as a socket whose data has not arrived yet does, so the reader's await
/// really suspends and resumes; <see cref="SuspendedReads"/> lets a setup prove it did. On .NET the
/// suspension reuses one <c>IValueTaskSource</c>, as a socket's awaitable event
/// args do, so it allocates nothing per read and the rung counts the reader's own allocations;
/// .NET Framework has no such source, and each read there costs a task, as a socket's does.
/// </summary>
internal sealed class ChunkedReadStream : Stream
#if NET
    , IValueTaskSource<int>, IThreadPoolWorkItem
#endif
{
    private readonly byte[] _body;
    private readonly int _chunkBytes;
    private readonly bool _suspend;
    private int _position;
#if NET
    private ManualResetValueTaskSourceCore<int> _pending;
    private Memory<byte> _pendingBuffer;
#endif

    public ChunkedReadStream(byte[] body, int chunkBytes, bool suspend = false)
    {
        ArgumentNullException.ThrowIfNull(body);
        ArgumentOutOfRangeException.ThrowIfLessThan(chunkBytes, 1);
        _body = body;
        _chunkBytes = chunkBytes;
        _suspend = suspend;
#if NET
        // The reader resumes on the worker that completed its read, as it does on a socket's I/O
        // thread, rather than after a second hop through the pool.
        _pending.RunContinuationsAsynchronously = false;
#endif
    }

    /// <summary>Gets how many asynchronous reads suspended before they returned.</summary>
    public int SuspendedReads { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => _body.Length;

    public override long Position
    {
        get => _position;
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) => ReadChunk(buffer.AsSpan(offset, count));

#if NET
    public override int Read(Span<byte> buffer) => ReadChunk(buffer);

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        ReadAsync(buffer.AsMemory(offset, count), cancellationToken).AsTask();

    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return ValueTask.FromCanceled<int>(cancellationToken);
        }

        if (!_suspend)
        {
            return ValueTask.FromResult(ReadChunk(buffer.Span));
        }

        // One read is outstanding at a time, as on a socket, so the one source is free again here.
        _pending.Reset();
        _pendingBuffer = buffer;
        ThreadPool.UnsafeQueueUserWorkItem(this, preferLocal: false);
        return new ValueTask<int>(this, _pending.Version);
    }

    /// <summary>The thread-pool half of a suspended read: the data "arrives" and the reader resumes.</summary>
    void IThreadPoolWorkItem.Execute()
    {
        SuspendedReads++;
        var count = ReadChunk(_pendingBuffer.Span);
        _pendingBuffer = default;
        _pending.SetResult(count);
    }

    int IValueTaskSource<int>.GetResult(short token) => _pending.GetResult(token);

    ValueTaskSourceStatus IValueTaskSource<int>.GetStatus(short token) => _pending.GetStatus(token);

    void IValueTaskSource<int>.OnCompleted(Action<object?> continuation, object? state, short token, ValueTaskSourceOnCompletedFlags flags) =>
        _pending.OnCompleted(continuation, state, token, flags);
#else
    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        if (cancellationToken.IsCancellationRequested)
        {
            return Task.FromCanceled<int>(cancellationToken);
        }

        return _suspend
            ? SuspendThenReadAsync(buffer, offset, count)
            : Task.FromResult(ReadChunk(buffer.AsSpan(offset, count)));
    }

    private async Task<int> SuspendThenReadAsync(byte[] buffer, int offset, int count)
    {
        await Task.Yield();
        SuspendedReads++;
        return ReadChunk(buffer.AsSpan(offset, count));
    }
#endif

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    private int ReadChunk(Span<byte> buffer)
    {
        var count = Math.Min(Math.Min(buffer.Length, _chunkBytes), _body.Length - _position);
        _body.AsSpan(_position, count).CopyTo(buffer);
        _position += count;
        return count;
    }
}
