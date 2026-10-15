namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// A body whose data never arrives: every asynchronous read stays pending until its token is
/// cancelled, as a quiet event stream's socket does, so a rung can measure what cancelling a read
/// that is really in flight costs. <see cref="PendingReads"/> lets a setup prove the read was
/// pending when it was cancelled.
/// </summary>
internal sealed class PendingReadStream : Stream
{
    /// <summary>Gets how many asynchronous reads were started.</summary>
    public int PendingReads { get; private set; }

    public override bool CanRead => true;

    public override bool CanSeek => false;

    public override bool CanWrite => false;

    public override long Length => throw new NotSupportedException();

    public override long Position
    {
        get => throw new NotSupportedException();
        set => throw new NotSupportedException();
    }

    public override int Read(byte[] buffer, int offset, int count) =>
        throw new NotSupportedException("A pending body is read asynchronously only.");

    public override Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken)
    {
        PendingReads++;
        return WaitForCancellationAsync(cancellationToken);
    }

#if NET
    public override ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default)
    {
        PendingReads++;
        return new ValueTask<int>(WaitForCancellationAsync(cancellationToken));
    }
#endif

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    /// <summary>A read whose data never arrives: it ends, cancelled, when its token is cancelled, and not before.</summary>
    private static async Task<int> WaitForCancellationAsync(CancellationToken cancellationToken)
    {
        var read = new TaskCompletionSource<int>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => read.TrySetCanceled(cancellationToken)))
        {
            return await read.Task.ConfigureAwait(false);
        }
    }
}
