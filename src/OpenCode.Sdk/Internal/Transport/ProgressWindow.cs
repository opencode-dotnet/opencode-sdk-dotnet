namespace OpenCode.Sdk.Internal;

/// <summary>
/// The progress window one exchange runs under: a token linked over the caller's, which a timer
/// on the pipeline's clock cancels when the window passes with no restart. The send and each
/// buffered read restart it as they progress, so a slow exchange that keeps moving survives and a
/// stalled one is interrupted. Knowledge source: BCL-derived — the timer cancels the source the
/// way <c>Microsoft.Bcl.TimeProvider</c>'s <c>CreateCancellationTokenSource</c> does, because a
/// linked source's own <c>CancelAfter</c> always runs on the system timer.
/// </summary>
internal sealed class ProgressWindow : IDisposable
{
    private readonly CancellationTokenSource _source;
    private readonly ITimer _timer;
    private readonly TimeSpan _length;

    /// <summary>Creates a window over the caller's token; it is armed by the first <see cref="Restart"/>.</summary>
    /// <param name="length">How long the exchange may go without progress.</param>
    /// <param name="time">The clock the window runs on.</param>
    /// <param name="callerToken">The caller's token, which cancels the window's token too.</param>
    public ProgressWindow(TimeSpan length, TimeProvider time, CancellationToken callerToken)
    {
        ArgumentNullException.ThrowIfNull(time);

        _length = length;
        var source = CancellationTokenSource.CreateLinkedTokenSource(callerToken);
        _source = source;
        _timer = time.CreateTimer(state => _ = TryCancel(source), state: null, Timeout.InfiniteTimeSpan, Timeout.InfiniteTimeSpan);
    }

    /// <summary>Gets the token network I/O runs under: canceled by the caller or by the window passing.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>Gets whether the caller or the window has interrupted the exchange.</summary>
    public bool IsCancellationRequested => _source.IsCancellationRequested;

    /// <summary>Starts the window again from now; a no-op once the window is disposed.</summary>
    public void Restart() => _ = _timer.Change(_length, Timeout.InfiniteTimeSpan);

    public void Dispose()
    {
        _timer.Dispose();
        _source.Dispose();
    }

    /// <summary>
    /// Cancels the window's source. A timer callback already in flight when the window is disposed
    /// finds the source disposed; the exchange is over by then, so there is nothing left to cancel.
    /// </summary>
    private static bool TryCancel(CancellationTokenSource source)
    {
        try
        {
            source.Cancel();
            return true;
        }
        catch (ObjectDisposedException)
        {
            return false;
        }
    }
}
