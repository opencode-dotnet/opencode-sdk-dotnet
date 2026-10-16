using System.Reflection;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Transport;

/// <summary>
/// A system timer can run a callback that was queued before the window was restarted or stopped.
/// The window cancels only when its own deadline has passed: a callback for an old deadline waits
/// out the new one, a callback after a stop does nothing, and a callback that runs early waits out
/// the rest. The clock here hands each callback to the test, which runs it when it chooses.
/// </summary>
public sealed class ProgressWindowTests
{
    private static readonly TimeSpan Window = TimeSpan.FromSeconds(10);

    [Test]
    public async Task OnTimer_Should_Declare_A_Reasoned_Best_Effort_Cancellation_Suppression()
    {
        var cancellation = typeof(ProgressWindow).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => method.GetParameters() is [{ ParameterType: var parameterType }]
                && parameterType == typeof(CancellationTokenSource));
        var suppressions = SuppressionDeclaration.BestEffortFailures(cancellation);

        await Assert.That(suppressions).Count().IsEqualTo(1);
        await Assert.That(suppressions.Single().Reason).IsEqualTo(
            "An in-flight timer can reach a source the completed exchange already disposed; disposal is observed by the owning exchange, which has no remaining work to cancel.");
    }

    [Test]
    public async Task A_Callback_At_The_Deadline_Should_Cancel()
    {
        var clock = new HandFiredClock();
        using var window = new ProgressWindow(Window, clock, CancellationToken.None);
        window.Restart();

        clock.Now += Window;
        clock.Fire();

        await Assert.That(window.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task A_Callback_Queued_Before_A_Stop_Should_Not_Cancel()
    {
        var clock = new HandFiredClock();
        using var window = new ProgressWindow(Window, clock, CancellationToken.None);
        window.Restart();

        clock.Now += Window;
        window.Stop();
        clock.Fire();

        await Assert.That(window.IsCancellationRequested).IsFalse();
        await Assert.That(clock.Due).IsEqualTo(Timeout.InfiniteTimeSpan);
    }

    [Test]
    public async Task A_Callback_Queued_Before_A_Restart_Should_Wait_Out_The_New_Deadline()
    {
        var clock = new HandFiredClock();
        using var window = new ProgressWindow(Window, clock, CancellationToken.None);
        window.Restart();

        clock.Now += Window;
        window.Restart();
        clock.Fire();
        await Assert.That(window.IsCancellationRequested).IsFalse();
        await Assert.That(clock.Due).IsEqualTo(Window);

        clock.Now += Window;
        clock.Fire();
        await Assert.That(window.IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task A_Callback_That_Runs_Early_Should_Wait_Out_The_Rest()
    {
        var clock = new HandFiredClock();
        using var window = new ProgressWindow(Window, clock, CancellationToken.None);
        window.Restart();

        clock.Now += Window - TimeSpan.FromMilliseconds(1);
        clock.Fire();

        await Assert.That(window.IsCancellationRequested).IsFalse();
        await Assert.That(clock.Due).IsEqualTo(TimeSpan.FromMilliseconds(1));
    }

    /// <summary>A clock whose one timer runs only when the test fires it, and reports what it was last armed for.</summary>
    private sealed class HandFiredClock : TimeProvider
    {
        private TimerCallback? _callback;
        private object? _state;

        public TimeSpan Now { get; set; }

        /// <summary>Gets the due time the timer was last armed with.</summary>
        public TimeSpan Due { get; private set; } = Timeout.InfiniteTimeSpan;

        public override long TimestampFrequency => TimeSpan.TicksPerSecond;

        public override long GetTimestamp() => Now.Ticks;

        public override ITimer CreateTimer(TimerCallback callback, object? state, TimeSpan dueTime, TimeSpan period)
        {
            _callback = callback;
            _state = state;
            Due = dueTime;
            return new Timer(this);
        }

        /// <summary>Runs the callback, as a timer whose callback was already queued does.</summary>
        public void Fire() => _callback!(_state);

        private sealed class Timer(HandFiredClock clock) : ITimer
        {
            public bool Change(TimeSpan dueTime, TimeSpan period)
            {
                clock.Due = dueTime;
                return true;
            }

            public void Dispose()
            {
            }

            public ValueTask DisposeAsync() => default;
        }
    }
}
