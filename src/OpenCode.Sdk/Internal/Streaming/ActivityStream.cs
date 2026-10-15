namespace OpenCode.Sdk.Internal;

/// <summary>
/// A read-only view of an event-stream body that reports every read bringing bytes, so the idle
/// window sees all traffic — keepalive comments included, which never become frames. Disposing the
/// view disposes the body, which the response would dispose a moment later anyway. Knowledge source:
/// upstream-observed — the pinned client re-arms its idle watchdog on any received bytes
/// (<c>onActivity</c>), not on decoded events.
/// </summary>
internal sealed class ActivityStream : Stream
{
    private readonly Stream _inner;
    private readonly Action? _onActivity;

    /// <summary>Creates the view.</summary>
    /// <param name="inner">The body the response owns.</param>
    /// <param name="onActivity">Runs after each read that returned at least one byte; <see langword="null"/> for none.</param>
    public ActivityStream(Stream inner, Action? onActivity)
    {
        ArgumentNullException.ThrowIfNull(inner);

        _inner = inner;
        _onActivity = onActivity;
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

    public override int Read(byte[] buffer, int offset, int count) => Observe(_inner.Read(buffer, offset, count));

    public override async Task<int> ReadAsync(byte[] buffer, int offset, int count, CancellationToken cancellationToken) =>
        Observe(await _inner.ReadAsync(buffer.AsMemory(offset, count), cancellationToken).ConfigureAwait(false));

#if NET
    public override async ValueTask<int> ReadAsync(Memory<byte> buffer, CancellationToken cancellationToken = default) =>
        Observe(await _inner.ReadAsync(buffer, cancellationToken).ConfigureAwait(false));
#endif

    public override void Flush()
    {
    }

    public override long Seek(long offset, SeekOrigin origin) => throw new NotSupportedException();

    public override void SetLength(long value) => throw new NotSupportedException();

    public override void Write(byte[] buffer, int offset, int count) => throw new NotSupportedException();

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            _inner.Dispose();
        }

        base.Dispose(disposing);
    }

    private int Observe(int read)
    {
        if (read > 0)
        {
            _onActivity?.Invoke();
        }

        return read;
    }
}
