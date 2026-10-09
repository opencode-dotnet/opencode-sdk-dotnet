using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using NSubstitute;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Internal.Windows.Abstractions;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Launcher;

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

    /// <summary>How much earlier than its due time a timed wait may end on a coarse system timer.</summary>
    private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(50);

    [Test]
    public async Task RunAsync_Should_Not_Run_The_Tree_Kill_For_A_Root_That_Exited()
    {
        var treeKill = Substitute.For<IWindowsTreeKill>();
        using var root = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        await new WindowsServerLadder(ProcessId, root, Task.FromResult(new ChildExitStatus { ExitCode = 7 }), treeKill, Bounds).RunAsync(Grace);

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

        var ladder = new WindowsServerLadder(ProcessId, root, exited.Task, treeKill, Bounds).RunAsync(unwaitedGrace);
        var killsBeforeAnyWait = treeKill.ReceivedCalls().ToList();
        _ = exited.TrySetResult(new ChildExitStatus { ExitCode = 1 });
        await ladder;

        await Assert.That(killsBeforeAnyWait).Count().IsEqualTo(1);
        _ = await treeKill.Received(1).KillTreeAsync(ProcessId, unwaitedGrace);
    }

    /// <summary>A kill that fails leaves the fallback; the root then ends inside the grace, and no second rung runs.</summary>
    [Test]
    public async Task RunAsync_Should_Run_No_Second_Rung_When_The_Root_Ends_Inside_The_Grace()
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the fallback calls TerminateProcess, which only Windows has");
            return;
        }

        var exited = new TaskCompletionSource<ChildExitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var treeKill = Substitute.For<IWindowsTreeKill>();
        _ = treeKill.KillTreeAsync(ProcessId, Arg.Any<TimeSpan>()).Returns(Task.FromResult(TreeKillOutcome.Failed));
        using var root = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        var ladder = new WindowsServerLadder(ProcessId, root, exited.Task, treeKill, Bounds).RunAsync(Grace);

        _ = exited.TrySetResult(new ChildExitStatus { ExitCode = 1 });
        await ladder;

        _ = await treeKill.Received(1).KillTreeAsync(Arg.Any<int>(), Arg.Any<TimeSpan>());
    }

    /// <summary>
    /// The root outlives the grace: the pair runs again once the grace, which started with the first
    /// kill, has passed, and then the ladder waits for the root at most its forced-exit bound.
    /// </summary>
    [Test]
    public async Task RunAsync_Should_Run_The_Second_Rung_Once_The_Grace_Passed()
    {
        var exited = new TaskCompletionSource<ChildExitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        var clock = Stopwatch.StartNew();
        var calls = new List<TimeSpan>();
        var treeKill = Substitute.For<IWindowsTreeKill>();
        _ = treeKill.KillTreeAsync(ProcessId, Arg.Any<TimeSpan>()).Returns(call =>
        {
            calls.Add(clock.Elapsed);
            return Task.FromResult(TreeKillOutcome.Ended);
        });
        using var root = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);

        await new WindowsServerLadder(ProcessId, root, exited.Task, treeKill, Bounds).RunAsync(Grace);
        var elapsed = clock.Elapsed;

        await Assert.That(calls.Count).IsEqualTo(2);
        await Assert.That(calls[1] - calls[0]).IsGreaterThanOrEqualTo(Grace - TimerSlack);
        _ = await treeKill.Received(1).KillTreeAsync(ProcessId, Grace);
        _ = await treeKill.Received(1).KillTreeAsync(ProcessId, Bounds.TreeKill);
        await Assert.That(elapsed).IsGreaterThanOrEqualTo(Grace + Bounds.ForcedExit - TimerSlack);
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

        await new WindowsServerLadder(ProcessId, root, exited.Task, treeKill, Bounds).RunAsync(TimeSpan.Zero);

        _ = await treeKill.Received(1).KillTreeAsync(Arg.Any<int>(), Arg.Any<TimeSpan>());
        _ = await treeKill.Received(1).KillTreeAsync(ProcessId, Bounds.TreeKill);
    }

    /// <summary>
    /// Nothing ends: the kill never returns and the root never exits. The grace bounds the first
    /// kill and its wait, the second kill has its own bound, and so does the last wait, so the ladder
    /// returns after the three of them and no later.
    /// </summary>
    [Test]
    [Timeout(30_000)]
    public async Task RunAsync_Should_Return_Within_Its_Bounds_When_Nothing_Ends(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the fallback calls TerminateProcess, which only Windows has");
            return;
        }

        var never = new TaskCompletionSource<TreeKillOutcome>(TaskCreationOptions.RunContinuationsAsynchronously);
        var treeKill = Substitute.For<IWindowsTreeKill>();
        _ = treeKill.KillTreeAsync(ProcessId, Arg.Any<TimeSpan>()).Returns(never.Task);
        var stillRunning = new TaskCompletionSource<ChildExitStatus>(TaskCreationOptions.RunContinuationsAsynchronously);
        using var root = new SafeProcessHandle(IntPtr.Zero, ownsHandle: false);
        var run = Stopwatch.StartNew();

        await new WindowsServerLadder(ProcessId, root, stillRunning.Task, treeKill, Bounds).RunAsync(Grace).WaitAsync(cancellationToken);
        var elapsed = run.Elapsed;

        var worstCase = Grace + Bounds.TreeKill + Bounds.ForcedExit;
        await Assert.That(elapsed).IsGreaterThanOrEqualTo(worstCase - TimerSlack);
        await Assert.That(elapsed).IsLessThan(worstCase + TimeSpan.FromSeconds(5));
        _ = await treeKill.Received(2).KillTreeAsync(Arg.Any<int>(), Arg.Any<TimeSpan>());
    }
}
