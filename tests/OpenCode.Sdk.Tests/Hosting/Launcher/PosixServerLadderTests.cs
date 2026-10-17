using System.Diagnostics;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.Launcher;

/// <summary>
/// Every rung of the POSIX disposal ladder against scripted seams, with no process: the signals are
/// a substitute, and the root's exit is a <see cref="ScriptedChildExit"/> the test (or a signal the
/// substitute receives) completes. The root ends once: a signal that reaches the group after the
/// root ended leaves its status alone, so the status the watch reports does not depend on when the
/// watch thread runs. The live proofs of the same rungs run against real process trees in
/// <c>OpenCodeServerProcessGroupTests</c>.
/// </summary>
public sealed class PosixServerLadderTests
{
    private const int Pid = 4242;

    /// <summary>
    /// A hang guard on the exit the watch observes, never a measure of speed: every wait follows an
    /// exit the test or the ladder already scripted, so only a watch that never reports it reaches
    /// the guard.
    /// </summary>
    private static readonly TimeSpan ExitObservationGuard = TimeSpan.FromSeconds(60);

    /// <summary>A grace no proof waits out: a ladder that reaches it has failed its test.</summary>
    private static readonly TimeSpan LongGrace = TimeSpan.FromSeconds(30);

    private static readonly TimeSpan ShortGrace = TimeSpan.FromMilliseconds(300);

    /// <summary>
    /// How much earlier than its due time a timed wait may end on a coarse system timer (Windows
    /// ticks every 15.6 ms); a ladder that skipped the grace would end hundreds of milliseconds early.
    /// </summary>
    private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(50);

    private static readonly ChildExitStatus Terminated = new() { Signal = 15 };
    private static readonly ChildExitStatus Killed = new() { Signal = 9 };

    [Test]
    public async Task RunAsync_Should_Stop_After_Sigterm_When_The_Root_And_Its_Group_End_Within_The_Grace()
    {
        using var exits = new ScriptedChildExit();
        var signals = Signals(exits);
        _ = signals.ProbeGroup(Pid).Returns(SignalDelivery.NoSuchTarget);
        var ladder = Ladder(exits, signals, out var watch);

        var elapsed = Stopwatch.StartNew();
        await ladder.RunAsync(LongGrace);

        _ = signals.Received(1).SignalGroup(Pid, ProcessSignal.Terminate);
        _ = signals.DidNotReceive().SignalGroup(Pid, ProcessSignal.Kill);
        await Assert.That(elapsed.Elapsed).IsLessThan(LongGrace);
        await Assert.That(await ChildExitObservation.WithinAsync(watch, ExitObservationGuard)).IsEqualTo(Terminated);
    }

    [Test]
    public async Task RunAsync_Should_Kill_The_Group_When_The_Root_Outlives_The_Grace()
    {
        using var exits = new ScriptedChildExit();
        var signals = Signals(exits, exitOnTerminate: false);
        var ladder = Ladder(exits, signals, out var watch);

        var elapsed = Stopwatch.StartNew();
        await ladder.RunAsync(ShortGrace);

        _ = signals.Received(1).SignalGroup(Pid, ProcessSignal.Terminate);
        _ = signals.Received(1).SignalGroup(Pid, ProcessSignal.Kill);
        await Assert.That(elapsed.Elapsed).IsGreaterThanOrEqualTo(ShortGrace - TimerSlack);
        await Assert.That(await ChildExitObservation.WithinAsync(watch, ExitObservationGuard)).IsEqualTo(Killed);
    }

    [Test]
    public async Task RunAsync_Should_Kill_The_Group_When_A_Member_Outlives_The_Grace_After_The_Root_Exited()
    {
        using var exits = new ScriptedChildExit();
        var signals = Signals(exits);
        _ = signals.ProbeGroup(Pid).Returns(SignalDelivery.Delivered);
        var ladder = Ladder(exits, signals, out var watch);

        var elapsed = Stopwatch.StartNew();
        await ladder.RunAsync(ShortGrace);

        _ = signals.Received(1).SignalGroup(Pid, ProcessSignal.Kill);
        _ = signals.DidNotReceive().SignalProcess(Pid, ProcessSignal.Kill);
        await Assert.That(elapsed.Elapsed).IsGreaterThanOrEqualTo(ShortGrace - TimerSlack);
        await Assert.That(await ChildExitObservation.WithinAsync(watch, ExitObservationGuard)).IsEqualTo(Terminated);
    }

    [Test]
    public async Task RunAsync_Should_Kill_At_Once_When_The_Grace_Is_Zero()
    {
        using var exits = new ScriptedChildExit();
        var signals = Signals(exits, exitOnTerminate: false);
        var ladder = Ladder(exits, signals, out _);

        await ladder.RunAsync(TimeSpan.Zero);

        Received.InOrder(() =>
        {
            _ = signals.SignalGroup(Pid, ProcessSignal.Terminate);
            _ = signals.SignalGroup(Pid, ProcessSignal.Kill);
        });
    }

    /// <summary>
    /// A root that exited with code 0 or on a signal leaves its group alone even with members left,
    /// as upstream leaves it; an unknown status leaves alone only a group that is already empty.
    /// </summary>
    [Test]
    [Arguments("exit code 0")]
    [Arguments("signal 15")]
    [Arguments("unknown")]
    public async Task RunAsync_Should_Leave_The_Group_Alone_When_The_Root_Already_Ended_Without_A_Failure_Code(string end)
    {
        using var exits = new ScriptedChildExit();
        var signals = Signals(exits);
        var unknown = string.Equals(end, "unknown", StringComparison.Ordinal);
        _ = signals.ProbeGroup(Pid).Returns(unknown ? SignalDelivery.NoSuchTarget : SignalDelivery.Delivered);
        var ladder = Ladder(exits, signals, out var watch);
        exits.Exit(end switch
        {
            "exit code 0" => new ChildExitStatus { ExitCode = 0 },
            "signal 15" => Terminated,
            _ => ChildExitStatus.Unknown,
        });
        _ = await ChildExitObservation.WithinAsync(watch, ExitObservationGuard);

        await ladder.RunAsync(LongGrace);

        _ = signals.DidNotReceive().SignalGroup(Arg.Any<int>(), Arg.Any<ProcessSignal>());
        _ = signals.DidNotReceive().SignalProcess(Arg.Any<int>(), Arg.Any<ProcessSignal>());
        _ = signals.Received(unknown ? 1 : 0).ProbeGroup(Pid);
    }

    /// <summary>
    /// Nobody could read the root's status (the kernel or a runtime that reaps every child took
    /// it), so the status decides nothing: the group is probed, and a member left in it is ended on
    /// the same rungs as behind a root that exited with a failure, <c>SIGKILL</c> included.
    /// </summary>
    [Test]
    public async Task RunAsync_Should_End_The_Survivors_When_The_Roots_Status_Is_Unknown_And_Its_Group_Has_Members()
    {
        using var exits = new ScriptedChildExit();
        var signals = Signals(exits);
        _ = signals.ProbeGroup(Pid).Returns(SignalDelivery.Delivered);
        var ladder = Ladder(exits, signals, out var watch);
        exits.Exit(ChildExitStatus.Unknown);
        _ = await ChildExitObservation.WithinAsync(watch, ExitObservationGuard);

        await ladder.RunAsync(ShortGrace);

        Received.InOrder(() =>
        {
            _ = signals.SignalGroup(Pid, ProcessSignal.Terminate);
            _ = signals.SignalGroup(Pid, ProcessSignal.Kill);
        });
        _ = signals.DidNotReceive().SignalProcess(Arg.Any<int>(), Arg.Any<ProcessSignal>());
    }

    [Test]
    public async Task RunAsync_Should_End_The_Survivors_When_The_Root_Already_Exited_With_A_Failure_Code()
    {
        using var exits = new ScriptedChildExit();
        var signals = Signals(exits);
        _ = signals.ProbeGroup(Pid).Returns(SignalDelivery.NoSuchTarget);
        var ladder = Ladder(exits, signals, out var watch);
        exits.Exit(new ChildExitStatus { ExitCode = 3 });
        _ = await ChildExitObservation.WithinAsync(watch, ExitObservationGuard);

        await ladder.RunAsync(LongGrace);

        _ = signals.Received(1).SignalGroup(Pid, ProcessSignal.Terminate);
        _ = signals.DidNotReceive().SignalGroup(Pid, ProcessSignal.Kill);
    }

    /// <summary>
    /// A root that exited with a failure and left no survivor has an empty group, and its pid is
    /// reaped: the fallback must not signal a pid that may already name another process.
    /// </summary>
    [Test]
    public async Task RunAsync_Should_Not_Signal_A_Reaped_Root_By_Its_Pid()
    {
        using var exits = new ScriptedChildExit();
        var signals = Signals(exits);
        _ = signals.SignalGroup(Pid, Arg.Any<ProcessSignal>()).Returns(SignalDelivery.NoSuchTarget);
        var ladder = Ladder(exits, signals, out var watch);
        exits.Exit(new ChildExitStatus { ExitCode = 3 });
        _ = await ChildExitObservation.WithinAsync(watch, ExitObservationGuard);

        await ladder.RunAsync(LongGrace);

        _ = signals.Received(1).SignalGroup(Pid, ProcessSignal.Terminate);
        _ = signals.DidNotReceive().SignalProcess(Arg.Any<int>(), Arg.Any<ProcessSignal>());
        _ = signals.DidNotReceive().SignalGroup(Pid, ProcessSignal.Kill);
    }

    [Test]
    public async Task RunAsync_Should_Not_Escalate_When_Neither_The_Group_Nor_The_Pid_Takes_Sigterm()
    {
        using var exits = new ScriptedChildExit();
        var signals = Substitute.For<IProcessGroupSignal>();
        _ = signals.SignalGroup(Pid, Arg.Any<ProcessSignal>()).Returns(SignalDelivery.Refused);
        _ = signals.SignalProcess(Pid, Arg.Any<ProcessSignal>()).Returns(SignalDelivery.Refused);
        var ladder = Ladder(exits, signals, out var watch);
        try
        {
            var elapsed = Stopwatch.StartNew();
            await ladder.RunAsync(LongGrace);

            await Assert.That(elapsed.Elapsed).IsLessThan(LongGrace);
            _ = signals.Received(1).SignalGroup(Pid, ProcessSignal.Terminate);
            _ = signals.Received(1).SignalProcess(Pid, ProcessSignal.Terminate);
            _ = signals.DidNotReceive().SignalGroup(Pid, ProcessSignal.Kill);
            _ = signals.DidNotReceive().SignalProcess(Pid, ProcessSignal.Kill);
        }
        finally
        {
            exits.Exit(ChildExitStatus.Unknown);
            _ = await ChildExitObservation.WithinAsync(watch, ExitObservationGuard);
        }
    }

    [Test]
    public async Task RunAsync_Should_Fall_Back_To_The_Pid_When_The_Group_Refuses_The_Signal()
    {
        using var exits = new ScriptedChildExit();
        var signals = Signals(exits);
        _ = signals.SignalGroup(Pid, Arg.Any<ProcessSignal>()).Returns(SignalDelivery.Refused);
        _ = signals.ProbeGroup(Pid).Returns(SignalDelivery.NoSuchTarget);
        var ladder = Ladder(exits, signals, out var watch);

        await ladder.RunAsync(LongGrace);

        _ = signals.Received(1).SignalProcess(Pid, ProcessSignal.Terminate);
        _ = signals.DidNotReceive().SignalProcess(Pid, ProcessSignal.Kill);
        await Assert.That(await ChildExitObservation.WithinAsync(watch, ExitObservationGuard)).IsEqualTo(Terminated);
    }

    [Test]
    public async Task RunAsync_Should_Spend_The_Root_Grace_On_The_Supplied_Clock()
    {
        var time = new ManualTimeProvider();
        using var exits = new ScriptedChildExit();
        var signals = Signals(exits, exitOnTerminate: false);
        var watch = PosixChildExitWatch.Start(Pid, exits, signals, childrenReapedAutomatically: false);
        var ladder = new PosixServerLadder(Pid, watch, signals, PosixServerLadder.ForcedExitTimeout, time);
        var running = ladder.RunAsync(LongGrace);
        try
        {
            await time.WaitForArmedAsync(LongGrace, HangGuard.Bound);
            await Assert.That(time.ArmedIn(LongGrace)).IsEqualTo(1);
            time.Advance(LongGrace - TimeSpan.FromTicks(1));
            _ = signals.DidNotReceive().SignalGroup(Pid, ProcessSignal.Kill);
            time.Advance(TimeSpan.FromTicks(1));
            await running.WaitAsync(HangGuard.Bound);
            await Assert.That(GroupSignals(signals)).IsEquivalentTo(
                [ProcessSignal.Terminate, ProcessSignal.Kill], CollectionOrdering.Matching);
            await Assert.That(await ChildExitObservation.WithinAsync(watch, ExitObservationGuard)).IsEqualTo(Killed);
        }
        finally
        {
            exits.Exit(Killed);
            _ = await ChildExitObservation.WithinAsync(watch, ExitObservationGuard);
            time.Advance(LongGrace + PosixServerLadder.ForcedExitTimeout);
            await running.WaitAsync(HangGuard.Bound);
        }
    }

    [Test]
    public async Task RunAsync_Should_Probe_Only_For_The_Grace_Remaining_On_The_Supplied_Clock()
    {
        var time = new ManualTimeProvider();
        using var exits = new ScriptedChildExit();
        var signals = Signals(exits);
        var watch = PosixChildExitWatch.Start(Pid, exits, signals, childrenReapedAutomatically: false);
        exits.Exit(new ChildExitStatus { ExitCode = 3 });
        _ = await ChildExitObservation.WithinAsync(watch, ExitObservationGuard);
        var groupAlive = true;
        var firstProbe = true;
        _ = signals.ProbeGroup(Pid).Returns(_ =>
        {
            if (firstProbe)
            {
                firstProbe = false;
                time.Advance(LongGrace - TimeSpan.FromMilliseconds(1));
            }

            return groupAlive ? SignalDelivery.Delivered : SignalDelivery.NoSuchTarget;
        });
        var ladder = new PosixServerLadder(Pid, watch, signals, PosixServerLadder.ForcedExitTimeout, time);
        var running = ladder.RunAsync(LongGrace);
        try
        {
            await time.WaitForArmedAsync(TimeSpan.FromMilliseconds(1), HangGuard.Bound);
            await Assert.That(time.ArmedIn(TimeSpan.FromMilliseconds(1))).IsEqualTo(1);
            _ = signals.DidNotReceive().SignalGroup(Pid, ProcessSignal.Kill);
            time.Advance(TimeSpan.FromMilliseconds(1));
            await running.WaitAsync(HangGuard.Bound);
            await Assert.That(GroupSignals(signals)).IsEquivalentTo(
                [ProcessSignal.Terminate, ProcessSignal.Kill], CollectionOrdering.Matching);
        }
        finally
        {
            groupAlive = false;
            time.Advance(LongGrace);
            await running.WaitAsync(HangGuard.Bound);
        }
    }

    [Test]
    public async Task RunAsync_Should_Bound_The_Forced_Root_Wait_On_The_Supplied_Clock()
    {
        var time = new ManualTimeProvider();
        using var exits = new ScriptedChildExit();
        var signals = Substitute.For<IProcessGroupSignal>();
        _ = signals.SignalGroup(Pid, Arg.Any<ProcessSignal>()).Returns(SignalDelivery.Delivered);
        var watch = PosixChildExitWatch.Start(Pid, exits, signals, childrenReapedAutomatically: false);
        var ladder = new PosixServerLadder(Pid, watch, signals, PosixServerLadder.ForcedExitTimeout, time);
        var running = ladder.RunAsync(TimeSpan.Zero);
        try
        {
            await time.WaitForArmedAsync(PosixServerLadder.ForcedExitTimeout, HangGuard.Bound);
            await Assert.That(time.ArmedIn(PosixServerLadder.ForcedExitTimeout)).IsEqualTo(1);
            time.Advance(PosixServerLadder.ForcedExitTimeout - TimeSpan.FromMilliseconds(1));
            await Assert.That(running.IsCompleted).IsFalse();
            time.Advance(TimeSpan.FromMilliseconds(1));
            await running.WaitAsync(HangGuard.Bound);
            await Assert.That(watch.Exited.IsCompleted).IsFalse();
            await Assert.That(GroupSignals(signals)).IsEquivalentTo(
                [ProcessSignal.Terminate, ProcessSignal.Kill], CollectionOrdering.Matching);
        }
        finally
        {
            exits.Exit(Killed);
            _ = await ChildExitObservation.WithinAsync(watch, ExitObservationGuard);
            await running.WaitAsync(HangGuard.Bound);
        }
    }

    private static IEnumerable<ProcessSignal> GroupSignals(IProcessGroupSignal signals) =>
        signals.ReceivedCalls().Where(call => call.GetMethodInfo().Name == nameof(IProcessGroupSignal.SignalGroup))
            .Select(call => call.GetArguments().OfType<ProcessSignal>().Single());

    /// <summary>
    /// A substitute whose group and pid signals reach the scripted root: <c>SIGTERM</c> ends it with
    /// signal 15 unless <paramref name="exitOnTerminate"/> is false, and <c>SIGKILL</c> ends it with
    /// signal 9. A root that already ended keeps the status it ended with, as the kernel keeps it: a
    /// later signal reaches only the rest of its group. The group probe reports an empty group unless
    /// a test says otherwise.
    /// </summary>
    private static IProcessGroupSignal Signals(ScriptedChildExit exits, bool exitOnTerminate = true)
    {
        var signals = Substitute.For<IProcessGroupSignal>();
        SignalDelivery Deliver(ProcessSignal signal)
        {
            // The scripted reap reads the status without taking it: a status means the root ended.
            if (exits.Reap(Pid) is null && (signal is ProcessSignal.Kill || exitOnTerminate))
            {
                exits.Exit(signal is ProcessSignal.Kill ? Killed : Terminated);
            }

            return SignalDelivery.Delivered;
        }

        _ = signals.SignalGroup(Pid, Arg.Any<ProcessSignal>()).Returns(call => Deliver(call.Arg<ProcessSignal>()));
        _ = signals.SignalProcess(Pid, Arg.Any<ProcessSignal>()).Returns(call => Deliver(call.Arg<ProcessSignal>()));
        _ = signals.ProbeGroup(Pid).Returns(SignalDelivery.NoSuchTarget);
        return signals;
    }

    private static PosixServerLadder Ladder(ScriptedChildExit exits, IProcessGroupSignal signals, out PosixChildExitWatch watch)
    {
        watch = PosixChildExitWatch.Start(Pid, exits, signals, childrenReapedAutomatically: false);
        return new PosixServerLadder(Pid, watch, signals, PosixServerLadder.ForcedExitTimeout, TimeProvider.System);
    }
}
