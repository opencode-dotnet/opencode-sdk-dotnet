using System.Threading.Channels;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// A clock that moves only when the test moves it, so a bound running on it is passed by the
/// test's decision and never by how long the host took. <see cref="Advance"/> runs every timer the
/// move reaches, in due order, on the caller's thread. Each timer created on it is reported, so a
/// test moves time only after the bound it means to pass is running: time moved before that timer
/// exists would never reach it.
/// </summary>
internal sealed class ManualTimeProvider : TimeProvider
{
    private readonly Lock _gate = new();
    private readonly List<ManualTimer> _timers = [];
    private readonly Channel<bool> _created = Channel.CreateUnbounded<bool>();
    private readonly List<(DateTimeOffset Due, int Count, TaskCompletionSource Armed)> _armedWaiters = [];
    private DateTimeOffset _now = new(2026, 1, 1, 0, 0, 0, TimeSpan.Zero);

    public override TimeZoneInfo LocalTimeZone => TimeZoneInfo.Utc;

    public override long TimestampFrequency => TimeSpan.TicksPerSecond;

    public override DateTimeOffset GetUtcNow()
    {
        lock (_gate)
        {
            return _now;
        }
    }

    public override long GetTimestamp() => GetUtcNow().UtcTicks;

    public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
    {
        ArgumentNullException.ThrowIfNull(callback);

        var timer = new ManualTimer(this, callback, state);
        lock (_gate)
        {
            _timers.Add(timer);
            timer.Schedule(_now, dueTime, period);
        }

        _ = _created.Writer.TryWrite(true);
        return timer;
    }

    /// <summary>Completes once one more timer than earlier calls observed has been created.</summary>
    /// <param name="hangGuard">Bounds a timer that is never created; it decides nothing else.</param>
    /// <returns>A task that completes once the timer exists.</returns>
    public Task WaitForTimerAsync(TimeSpan hangGuard) => _created.Reader.ReadAsync().AsTask().WaitAsync(hangGuard);

    /// <summary>Counts timers armed to run exactly <paramref name="dueIn"/> from the clock's current time.</summary>
    /// <param name="dueIn">How far from now the timers must be due.</param>
    /// <returns>How many timers are armed for that instant.</returns>
    public int ArmedIn(TimeSpan dueIn)
    {
        lock (_gate)
        {
            var due = _now + dueIn;
            return _timers.Count(timer => timer.Due == due);
        }
    }

    /// <summary>
    /// Completes once <paramref name="count"/> timers are armed to run exactly
    /// <paramref name="dueIn"/> from the clock's current time, or at once when they already are.
    /// Unlike counting created timers, this names the bound a test means to pass, whichever object
    /// arms it and however often it is re-armed; the count tells apart two bounds that fall due at
    /// the same instant.
    /// </summary>
    /// <param name="dueIn">How far from now the timers must be due.</param>
    /// <param name="hangGuard">Bounds a timer that is never armed; it decides nothing else.</param>
    /// <param name="count">How many timers must be due then.</param>
    /// <returns>A task that completes once that many such timers are armed.</returns>
    public Task WaitForArmedAsync(TimeSpan dueIn, TimeSpan hangGuard, int count = 1)
    {
        var armed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        lock (_gate)
        {
            var due = _now + dueIn;
            if (_timers.Count(timer => timer.Due == due) >= count)
            {
                return Task.CompletedTask;
            }

            _armedWaiters.Add((due, count, armed));
        }

        return armed.Task.WaitAsync(hangGuard);
    }

    /// <summary>
    /// Moves the clock forward by <paramref name="delta"/> and runs, in due order, every timer the
    /// move reaches; a periodic timer runs once for each period the move covers.
    /// </summary>
    /// <param name="delta">How far the clock moves.</param>
    public void Advance(TimeSpan delta)
    {
        ArgumentOutOfRangeException.ThrowIfLessThan(delta, TimeSpan.Zero);

        DateTimeOffset target;
        lock (_gate)
        {
            target = _now + delta;
        }

        while (true)
        {
            ManualTimer? next;
            lock (_gate)
            {
                next = _timers.Where(timer => timer.Due <= target).OrderBy(timer => timer.Due).FirstOrDefault();
                if (next is null)
                {
                    _now = target;
                    return;
                }

                _now = next.Due!.Value;
                next.Reschedule();
            }

            // Outside the lock: a callback may restart or dispose its own timer, or read the clock.
            next.Run();
        }
    }

    /// <summary>Releases every waiter for a timer due at <paramref name="due"/>. Called under the clock's lock.</summary>
    private void NotifyArmed(DateTimeOffset? due)
    {
        if (due is null)
        {
            return;
        }

        var armedThen = _timers.Count(timer => timer.Due == due);
        foreach (var waiter in _armedWaiters.Where(waiter => waiter.Due == due && waiter.Count <= armedThen).ToArray())
        {
            _ = _armedWaiters.Remove(waiter);
            _ = waiter.Armed.TrySetResult();
        }
    }

    private void Remove(ManualTimer timer)
    {
        lock (_gate)
        {
            _ = _timers.Remove(timer);
        }
    }

    private sealed class ManualTimer(ManualTimeProvider clock, TimerCallback callback, object? state) : ITimer
    {
        private TimeSpan _period = Timeout.InfiniteTimeSpan;
        private bool _disposed;

        /// <summary>Gets when the timer runs next, or null while it is stopped. Read and written under the clock's lock.</summary>
        public DateTimeOffset? Due { get; private set; }

        public bool Change(TimeSpan dueTime, TimeSpan period)
        {
            lock (clock._gate)
            {
                if (_disposed)
                {
                    return false;
                }

                Schedule(clock._now, dueTime, period);
                return true;
            }
        }

        /// <summary>Sets the next run from <paramref name="now"/>, as a system timer reads its due time and period.</summary>
        public void Schedule(DateTimeOffset now, TimeSpan dueTime, TimeSpan period)
        {
            Due = dueTime == Timeout.InfiniteTimeSpan ? null : now + dueTime;
            _period = period;
            clock.NotifyArmed(Due);
        }

        /// <summary>After a run: the next period for a periodic timer; stopped otherwise.</summary>
        public void Reschedule() =>
            Due = _period == Timeout.InfiniteTimeSpan || _period <= TimeSpan.Zero ? null : Due + _period;

        public void Run() => callback(state);

        public void Dispose()
        {
            lock (clock._gate)
            {
                _disposed = true;
                Due = null;
            }

            clock.Remove(this);
        }

        public ValueTask DisposeAsync()
        {
            Dispose();
            return default;
        }
    }
}
