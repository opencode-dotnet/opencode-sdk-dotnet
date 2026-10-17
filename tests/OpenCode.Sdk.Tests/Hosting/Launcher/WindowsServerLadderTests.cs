using System.Collections.Concurrent;
using Microsoft.Win32.SafeHandles;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Hosting.Launcher;

/// <summary>
/// The Windows ladder's decisions, over a substituted tree kill and an exit the test completes: no
/// kill on a root that exited; the tree kill at once, bounded by the grace, which starts with it;
/// <c>TerminateProcess</c> when the kill fails; the same pair again when the root outlived the
/// grace; and every wait bounded, whatever the kill and the root do. The root handle is an empty
/// one, so the fallback reaches no process; its real effect is proven against real processes in the
/// launcher tests. Pure decisions over seams, so they run on every platform, apart from the two that
/// reach the fallback's <c>TerminateProcess</c>.
/// </summary>
public sealed class WindowsServerLadderTests
{
    private const int ProcessId = 4242;

    private static readonly TimeSpan Grace = TimeSpan.FromMilliseconds(400);

    /// <summary>Short second-rung bounds, so a bound that holds is seen to hold quickly.</summary>
    private static readonly WindowsLadderBounds Bounds = new(TimeSpan.FromMilliseconds(300), TimeSpan.FromMilliseconds(300));

    [Test]
    public async Task RunAsync_Should_Not_Run_The_Tree_Kill_For_A_Root_That_Exited()
    {
        var treeKill = Substitute.For<IWindowsTreeKill>();
        using var root = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        await new WindowsServerLadder(ProcessId, root, Task.FromResult(new ChildExitStatus { ExitCode = 7 }), treeKill, Bounds, TimeProvider.System).RunAsync(Grace);

        _ = await treeKill.DidNotReceive().KillTreeAsync(Arg.Any<int>(), Arg.Any<TimeSpan>());
    }

    /// <summary>
    /// The first kill runs before the ladder waits for anything: it is already recorded when the run
    /// first yields, while the root still runs and the whole grace lies ahead, so a ladder that waited
    /// before its first rung fails here without any clock being read.
    /// </summary>
    [Test]
    public async Task RunAsync_Should_End_The_Tree_At_Once_With_The_Grace_As_Its_Bound()
    {
        var unwaitedGrace = TimeSpan.FromSeconds(60);
        var exited = new TaskCompletionSource<ChildExitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var treeKill = Substitute.For<IWindowsTreeKill>();
        _ = treeKill.KillTreeAsync(ProcessId, Arg.Any<TimeSpan>()).Returns(Task.FromResult(TreeKillOutcome.Ended));
        using var root = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        var ladder = new WindowsServerLadder(ProcessId, root, exited.Task, treeKill, Bounds, TimeProvider.System).RunAsync(unwaitedGrace);
        var killsBeforeAnyWait = treeKill.ReceivedCalls().ToList();
        _ = exited.TrySetResult(new ChildExitStatus { ExitCode = 1 });
        await ladder;

        await Assert.That(killsBeforeAnyWait).Count().IsEqualTo(1);
        _ = await treeKill.Received(1).KillTreeAsync(ProcessId, unwaitedGrace);
    }

    /// <summary>
    /// A kill that fails leaves the fallback; the root then ends inside the grace, and no second rung
    /// runs. The grace is long enough that no start-up cost can use it up before the root ends, so
    /// the root's exit, not the clock, is what the ladder answers. The failed kill is already
    /// complete, so the first rung and its fallback run synchronously and the run first yields in
    /// the wait for the root: the exit lands there, after the one kill, while the ladder still runs.
    /// </summary>
    [Test]
    public async Task RunAsync_Should_Run_No_Second_Rung_When_The_Root_Ends_Inside_The_Grace()
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the fallback calls TerminateProcess, which only Windows has");
            return;
        }

        var unwaitedGrace = TimeSpan.FromSeconds(60);
        var exited = new TaskCompletionSource<ChildExitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var treeKill = Substitute.For<IWindowsTreeKill>();
        _ = treeKill.KillTreeAsync(ProcessId, Arg.Any<TimeSpan>()).Returns(Task.FromResult(TreeKillOutcome.Failed));
        using var root = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        var ladder = new WindowsServerLadder(ProcessId, root, exited.Task, treeKill, Bounds, TimeProvider.System).RunAsync(unwaitedGrace);
        var waitingAfterTheFirstRung = !ladder.IsCompleted;
        var killsBeforeTheExit = treeKill.ReceivedCalls().ToList();

        _ = exited.TrySetResult(new ChildExitStatus { ExitCode = 1 });
        await ladder;

        await Assert.That(waitingAfterTheFirstRung).IsTrue();
        await Assert.That(killsBeforeTheExit).Count().IsEqualTo(1);
        _ = await treeKill.Received(1).KillTreeAsync(Arg.Any<int>(), Arg.Any<TimeSpan>());
    }

    /// <summary>
    /// The root outlives the grace: the pair runs again once the grace, which starts with the first
    /// kill, has passed on the ladder's clock, and not a tick earlier; then the ladder waits for the
    /// root at most its forced-exit bound. The clock moves only when the test moves it, so the interval
    /// asserted is the one the ladder enforced, whatever the host's load.
    /// </summary>
    [Test]
    public async Task RunAsync_Should_Run_The_Second_Rung_Once_The_Grace_Passed()
    {
        var hangGuard = TimeSpan.FromSeconds(30);
        var time = new ManualTimeProvider();
        var exited = new TaskCompletionSource<ChildExitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var kills = new ConcurrentQueue<DateTimeOffset>();
        var treeKill = Substitute.For<IWindowsTreeKill>();
        _ = treeKill.KillTreeAsync(ProcessId, Arg.Any<TimeSpan>()).Returns(call =>
        {
            kills.Enqueue(time.GetUtcNow());
            return Task.FromResult(TreeKillOutcome.Ended);
        });
        using var root = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        var started = time.GetUtcNow();

        var ladder = new WindowsServerLadder(ProcessId, root, exited.Task, treeKill, Bounds, time).RunAsync(Grace);
        await time.WaitForTimerAsync(hangGuard);
        var graceArmed = time.ArmedIn(Grace);
        time.Advance(Grace - TimeSpan.FromTicks(1));
        var graceStillArmed = time.ArmedIn(TimeSpan.FromTicks(1));
        time.Advance(TimeSpan.FromTicks(1));
        await time.WaitForTimerAsync(hangGuard);
        var forcedExitArmed = time.ArmedIn(Bounds.ForcedExit);
        time.Advance(Bounds.ForcedExit - TimeSpan.FromTicks(1));
        var forcedExitStillArmed = time.ArmedIn(TimeSpan.FromTicks(1));
        var endedBeforeTheForcedExitBound = ladder.IsCompleted;
        time.Advance(TimeSpan.FromTicks(1));
        await ladder.WaitAsync(hangGuard);

        await Assert.That(graceArmed).IsGreaterThanOrEqualTo(1);
        await Assert.That(graceStillArmed).IsEqualTo(graceArmed);
        await Assert.That(forcedExitArmed).IsGreaterThanOrEqualTo(1);
        await Assert.That(forcedExitStillArmed).IsEqualTo(forcedExitArmed);
        var killedAt = kills.ToArray();
        await Assert.That(killedAt).Count().IsEqualTo(2);
        await Assert.That(killedAt[0]).IsEqualTo(started);
        await Assert.That(killedAt[1]).IsEqualTo(started + Grace);
        await Assert.That(endedBeforeTheForcedExitBound).IsFalse();
        _ = await treeKill.Received(1).KillTreeAsync(ProcessId, Grace);
        _ = await treeKill.Received(1).KillTreeAsync(ProcessId, Bounds.TreeKill);
    }

    /// <summary>A grace of zero leaves no time for the first rung: the second runs at once, alone.</summary>
    [Test]
    public async Task RunAsync_Should_Run_Only_The_Second_Rung_With_No_Grace()
    {
        var exited = new TaskCompletionSource<ChildExitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var treeKill = Substitute.For<IWindowsTreeKill>();
        _ = treeKill.KillTreeAsync(ProcessId, Arg.Any<TimeSpan>()).Returns(call =>
        {
            _ = exited.TrySetResult(new ChildExitStatus { ExitCode = 1 });
            return Task.FromResult(TreeKillOutcome.Ended);
        });
        using var root = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        await new WindowsServerLadder(ProcessId, root, exited.Task, treeKill, Bounds, TimeProvider.System).RunAsync(TimeSpan.Zero);

        _ = await treeKill.Received(1).KillTreeAsync(Arg.Any<int>(), Arg.Any<TimeSpan>());
        _ = await treeKill.Received(1).KillTreeAsync(ProcessId, Bounds.TreeKill);
    }

    /// <summary>
    /// Nothing ends: the kill never returns and the root never exits. The grace bounds the first
    /// kill and its wait, the second kill has its own bound, and so does the last wait: one tick
    /// before each bound the ladder still waits, and once the last has passed it returns with no
    /// further move of the clock. The clock moves only when the test moves it, so the bounds asserted
    /// are the ones the ladder enforced, whatever the host's load.
    /// </summary>
    [Test]
    public async Task RunAsync_Should_Return_Within_Its_Bounds_When_Nothing_Ends()
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the fallback calls TerminateProcess, which only Windows has");
            return;
        }

        var hangGuard = TimeSpan.FromSeconds(30);
        var time = new ManualTimeProvider();
        var never = new TaskCompletionSource<TreeKillOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var kills = new ConcurrentQueue<DateTimeOffset>();
        var treeKill = Substitute.For<IWindowsTreeKill>();
        _ = treeKill.KillTreeAsync(ProcessId, Arg.Any<TimeSpan>()).Returns(never.Task).AndDoes(_ => kills.Enqueue(time.GetUtcNow()));
        var stillRunning = new TaskCompletionSource<ChildExitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var root = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        var started = time.GetUtcNow();

        var ladder = new WindowsServerLadder(ProcessId, root, stillRunning.Task, treeKill, Bounds, time).RunAsync(Grace);
        var armedBefore = new List<int>();
        var armedOneTickBefore = new List<int>();
        foreach (var bound in new[] { Grace, Bounds.TreeKill, Bounds.ForcedExit })
        {
            await time.WaitForTimerAsync(hangGuard);
            armedBefore.Add(time.ArmedIn(bound));
            time.Advance(bound - TimeSpan.FromTicks(1));
            armedOneTickBefore.Add(time.ArmedIn(TimeSpan.FromTicks(1)));
            time.Advance(TimeSpan.FromTicks(1));
        }

        await ladder.WaitAsync(hangGuard);

        await Assert.That(armedBefore.Min()).IsGreaterThanOrEqualTo(1);
        await Assert.That(armedOneTickBefore).IsEquivalentTo(armedBefore, CollectionOrdering.Matching);
        await Assert.That(kills.ToArray()).IsEquivalentTo([started, started + Grace], CollectionOrdering.Matching);
        _ = await treeKill.Received(1).KillTreeAsync(ProcessId, Grace);
        _ = await treeKill.Received(1).KillTreeAsync(ProcessId, Bounds.TreeKill);
    }
}
