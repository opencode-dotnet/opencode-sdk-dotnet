namespace OpenCode.Sdk.Internal;

/// <summary>
/// The progress window one exchange runs under: a token linked over the caller's, which a timer
/// on the pipeline's clock cancels when the window passes with no restart. The send and each
/// buffered read restart it as they progress, so a slow exchange that keeps moving survives and a
/// stalled one is interrupted. The window keeps its own deadline, and the timer cancels only when
/// that deadline has passed: a callback already queued when the window was restarted or stopped
/// finds the deadline moved and waits out the rest, or finds no deadline and does nothing.
/// Knowledge source: BCL-derived — the timer cancels the source the way
/// <c>Microsoft.Bcl.TimeProvider</c>'s <c>CreateCancellationTokenSource</c> does, because a linked
/// source's own <c>CancelAfter</c> always runs on the system timer.
/// </summary>
internal sealed class ProgressWindow : IDisposable
{
    private const long NoDeadline = long.MaxValue;

    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _source;
    private readonly ITimer _timer;
    private readonly TimeProvider _time;
    private readonly TimeSpan _length;
    private long _deadline = NoDeadline;

    /// <summary>Creates a window over the caller's token; it is armed by the first <see cref="Restart()"/>.</summary>
    /// <param name="length">How long the exchange may go without progress.</param>
    /// <param name="time">The clock the window runs on.</param>
    /// <param name="callerToken">The caller's token, which cancels the window's token too.</param>
    public ProgressWindow(TimeSpan length, TimeProvider time, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(time);

        _length = length;
        _time = time;
        _source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _timer = time.CreateTimer(_ => OnTimer(), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Gets the token network I/O runs under: canceled by the caller or by the window passing.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>Gets whether the caller or the window has interrupted the exchange.</summary>
    public bool IsCancellationRequested => _source.IsCancellationRequested;

    /// <summary>Starts the window again from now; a no-op once the window is disposed.</summary>
    public void Restart() => Restart(_length);

    /// <summary>Starts the window again from now with a length of its own, for this run only; a no-op once disposed.</summary>
    /// <param name="length">How long the exchange may go without progress this time.</param>
    public void Restart(TimeSpan length)
    {
        lock (_gate)
        {
            _deadline = length == Timeout.InfiniteTimeSpan ? NoDeadline : _time.GetTimestamp() + ToTimestamp(length);
            _ = _timer.Change(length, Timeout.InfiniteTimeSpan);
        }
    }

    /// <summary>Stops the window without cancelling it, until the next <see cref="Restart()"/>; a no-op once disposed.</summary>
    public void Stop()
    {
        lock (_gate)
        {
            _deadline = NoDeadline;
            _ = _timer.Change(Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
        }
    }

    public void Dispose()
    {
        _timer.Dispose();
        _source.Dispose();
    }

    /// <summary>
    /// Cancels once the deadline has passed. A callback that runs for a deadline since moved later
    /// re-arms the timer for what is left of it; one that runs after a stop does nothing.
    /// </summary>
    private void OnTimer()
    {
        lock (_gate)
        {
            if (_deadline == NoDeadline)
            {
                return;
            }

            var remaining = _deadline - _time.GetTimestamp();
            if (remaining > 0)
            {
                _ = _timer.Change(FromTimestamp(remaining), Timeout.InfiniteTimeSpan);
                return;
            }

            _deadline = NoDeadline;
            Cancel(_source);
        }
    }

    private long ToTimestamp(TimeSpan length) =>
        (long)Math.Ceiling(length.Ticks * ((double)_time.TimestampFrequency / TimeSpan.TicksPerSecond));

    private TimeSpan FromTimestamp(long timestamp) =>
        TimeSpan.FromTicks((long)Math.Ceiling(timestamp * ((double)TimeSpan.TicksPerSecond / _time.TimestampFrequency)));

    /// <summary>
    /// Cancels the window's source. A timer callback already in flight when the window is disposed
    /// finds the source disposed; the exchange is over by then, so there is nothing left to cancel.
    /// </summary>
    [SlopwatchSuppress("SW003", "An in-flight timer can reach a source the completed exchange already disposed; disposal is observed by the owning exchange, which has no remaining work to cancel.")]
    private static void Cancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
        }
        catch (ObjectDisposedException)
        {
            // The owning exchange has already observed disposal and has no remaining work to cancel.
        }
    }
}
