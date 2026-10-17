namespace OpenCode.Sdk.Internal;

/// <summary>Creates provider-driven deadlines that release their timers when disposed early.</summary>
/// <remarks>
/// The downlevel BCL extension releases its separate timer only on cancellation, so its timer
/// outlives a source disposed before the deadline. The modern constructor owns its timer already.
/// After disposal both paths promise only that a late timer callback never throws; whether the
/// token then reads as canceled is unspecified (the modern constructor may cancel it).
/// </remarks>
internal static class TimedCancellation
{
    /// <summary>Creates a deadline whose source owns the provider's timer until disposal.</summary>
    /// <param name="timeout">How long until cancellation.</param>
    /// <param name="time">The clock that drives the deadline.</param>
    /// <returns>A source that cancels at the deadline and releases its timer on disposal.</returns>
    public static CancellationTokenSource Create(TimeSpan timeout, TimeProvider time) =>
#if NET
        new(timeout, time);
#else
        new TimerCancellationTokenSource(timeout, time);
#endif

#if !NET
    /// <summary>Owns the timer that the downlevel BCL extension leaves armed after early disposal.</summary>
    private sealed class TimerCancellationTokenSource : CancellationTokenSource
    {
        private readonly Lock _gate = new();
        private readonly ITimer? _timer;
        private bool _disposed;

        public TimerCancellationTokenSource(TimeSpan timeout, TimeProvider time) =>
            _timer = time.CreateTimer(static state =>
            {
                // The timer's state is always its owning source.
                var source = (TimerCancellationTokenSource)state;
                // The lock prevents cancel/dispose overlap; callbacks run under it, so waiting on another thread disposing this deadline would deadlock (nothing in the SDK does that).
                lock (source._gate)
                {
                    if (!source._disposed)
                    {
                        source.Cancel();
                    }
                }
            }, this, timeout, Timeout.InfiniteTimeSpan);

        protected override void Dispose(bool disposing)
        {
            if (disposing)
            {
                lock (_gate)
                {
                    if (_disposed)
                    {
                        return;
                    }

                    _disposed = true;
                }

                _timer?.Dispose();
            }

            base.Dispose(disposing);
        }
    }
#endif
}
