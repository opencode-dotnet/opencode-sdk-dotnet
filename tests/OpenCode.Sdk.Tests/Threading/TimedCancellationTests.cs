using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Threading;

public sealed class TimedCancellationTests
{
    private static readonly TimeSpan Timeout = TimeSpan.FromSeconds(2);

    [Test]
    public async Task Create_Should_Disarm_The_Timer_When_Disposed_Before_The_Deadline()
    {
        var time = new ManualTimeProvider();
        using var deadline = TimedCancellation.Create(Timeout, time);
        deadline.Dispose();

        await Assert.That(time.ArmedIn(Timeout)).IsEqualTo(0);
        deadline.Dispose();
    }

    [Test]
    public async Task Create_Should_Cancel_At_The_Deadline_And_Not_One_Tick_Before()
    {
        var time = new ManualTimeProvider();
        using var deadline = TimedCancellation.Create(Timeout, time);
        var cancellations = 0;
        using var registration = deadline.Token.Register(() => cancellations++);

        time.Advance(Timeout - TimeSpan.FromTicks(1));
        await Assert.That(deadline.IsCancellationRequested).IsFalse();
        await Assert.That(cancellations).IsEqualTo(0);

        time.Advance(TimeSpan.FromTicks(1));
        await Assert.That(deadline.IsCancellationRequested).IsTrue();
        await Assert.That(cancellations).IsEqualTo(1);
    }

#if !NET
    [Test]
    public async Task Create_Should_Keep_The_Timer_Until_An_In_Flight_Cancellation_Callback_Returns()
    {
        var time = new InFlightTimeProvider();
        using var deadline = TimedCancellation.Create(Timeout, time);
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var registration = deadline.Token.Register(() =>
        {
            entered.SetResult(true);
            release.Wait();
        });
        var firing = DedicatedThread.Run(time.Fire);
        var disposing = Task.CompletedTask;
        var disposedWhileHeld = false;
        try
        {
            await entered.Task.WaitAsync(HangGuard.Bound);
            disposing = DedicatedThread.Run(deadline.Dispose, out var disposer);
            // Before Dispose this thread has no wait; WaitSleepJoin proves lock contention.
            var progress = Task.Run(async () =>
            {
                while (!time.TimerDisposed && !disposer.ThreadState.HasFlag(ThreadState.WaitSleepJoin))
                {
                    await Task.Yield();
                }

                return time.TimerDisposed;
            });
            disposedWhileHeld = await progress.WaitAsync(HangGuard.Bound);
        }
        finally
        {
            release.Set();
            await Task.WhenAll(firing, disposing).WaitAsync(HangGuard.Bound);
        }

        await Assert.That(disposedWhileHeld).IsFalse();
        await Assert.That(time.TimerDisposed).IsTrue();
    }

    [Test]
    public async Task Create_Should_Ignore_A_Timer_Callback_Queued_After_Disposal()
    {
        var time = new InFlightTimeProvider();
        using var deadline = TimedCancellation.Create(Timeout, time);
        deadline.Dispose();

        // A disposed deadline promises only that a late callback never throws; whether its token
        // then reads as canceled is unspecified, as with the modern constructor.
        await Assert.That(time.Fire).ThrowsNothing();
    }
#endif

    [Test]
    public async Task Create_Should_Let_A_Timer_Callback_With_A_Registration_Race_Disposal()
    {
        for (var round = 0; round < 1_000; round++)
        {
            var time = new InFlightTimeProvider();
            using var deadline = TimedCancellation.Create(Timeout, time);
            // A registration makes the callback's Cancel run work under the lock while Dispose contends for it.
            using var registration = deadline.Token.Register(static () => { });
            using var barrier = new Barrier(2);
            var firing = DedicatedThread.Run(() =>
            {
                var met = barrier.SignalAndWait(HangGuard.Bound);
                time.Fire();
                return met;
            });
            var disposing = DedicatedThread.Run(() =>
            {
                var met = barrier.SignalAndWait(HangGuard.Bound);
                deadline.Dispose();
                return met;
            });

            var rendezvous = await Task.WhenAll(firing, disposing).WaitAsync(HangGuard.Bound);
            await Assert.That(rendezvous[0]).IsTrue();
            await Assert.That(rendezvous[1]).IsTrue();
        }
    }

    [Test]
    public async Task Create_Should_Allow_A_Cancellation_Callback_To_Dispose_Its_Own_Deadline()
    {
        var time = new ManualTimeProvider();
        using var deadline = TimedCancellation.Create(Timeout, time);
        var token = deadline.Token;
        var callbacksCompleted = 0;
        using var registration = token.Register(() =>
        {
            deadline.Dispose();
            callbacksCompleted++;
        });

        await Task.Run(() => time.Advance(Timeout)).WaitAsync(HangGuard.Bound);

        await Assert.That(token.IsCancellationRequested).IsTrue();
        await Assert.That(callbacksCompleted).IsEqualTo(1);
        await Assert.That(time.ArmedIn(TimeSpan.Zero)).IsEqualTo(0);
    }

    /// <summary>Retains a queued callback even after disposal, as a thread-pool timer can.</summary>
    private sealed class InFlightTimeProvider : TimeProvider
    {
        private Action? _fire;
#if !NET
        private InFlightTimer? _timer;

        public bool TimerDisposed => _timer?.IsDisposed == true;
#endif

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            var timer = new InFlightTimer(callback, state);
#if !NET
            _timer = timer;
#endif
            _fire = timer.Fire;
            return timer;
        }

        public void Fire() => _fire!();

        private sealed class InFlightTimer(TimerCallback callback, object? state) : ITimer
        {
            private volatile bool _disposed;

#if !NET
            public bool IsDisposed => _disposed;
#endif

            public bool Change(TimeSpan dueTime, TimeSpan period) => !_disposed;

            public void Fire() => callback(state);

            public void Dispose() => _disposed = true;

            public ValueTask DisposeAsync()
            {
                Dispose();
                return default;
            }
        }
    }
}
