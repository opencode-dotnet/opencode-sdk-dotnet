using System.Diagnostics;
using System.Globalization;
using NSubstitute;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Posix.Abstractions;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

/// <summary>
/// The standalone launcher against real process trees (<c>ladder-tree.js</c>): where the server
/// lives in the process tree, and how disposal and a failed start end it and its children. On Linux
/// and macOS the server leads a session of its own, disposal sends <c>SIGTERM</c> to its process
/// group, waits the grace for the root and then for the group, and sends <c>SIGKILL</c> to what is
/// left; a child in a session of its own is left to the server. On Windows the server is in the
/// launcher's job, and disposal ends the whole tree at once with <c>taskkill /T /F</c>, detached
/// children included, as upstream does; the grace bounds that first kill, and the same kill runs
/// again only when the root outlived it. Every process a tree reports is ended by its pid before the
/// test returns. Keyless <c>[NotInParallel]</c>: the proofs ride wall-clock bounds.
/// </summary>
[NotInParallel]
public sealed class OpenCodeServerProcessGroupTests
{
    /// <summary>A grace the normal close never needs: the stand-in ends on <c>SIGTERM</c> at once.</summary>
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    /// <summary>The grace a proof waits out on purpose.</summary>
    private static readonly TimeSpan ShortGrace = TimeSpan.FromSeconds(1);

    /// <summary>A grace far longer than <see cref="ObservationBound"/>, for a close that must not wait for it.</summary>
    private static readonly TimeSpan UnwaitedGrace = TimeSpan.FromSeconds(60);

    /// <summary>How long a test waits for a process it observes to end.</summary>
    private static readonly TimeSpan ObservationBound = TimeSpan.FromSeconds(10);

    /// <summary>How much earlier than its due time a timed wait may end on a coarse system timer.</summary>
    private static readonly TimeSpan TimerSlack = TimeSpan.FromMilliseconds(50);

    /// <summary><c>SIGTERM</c>: what a stand-in with no handler of its own dies of at a normal close.</summary>
    private const int Terminate = 15;

    /// <summary><c>SIGKILL</c>: what a stand-in that outlived the grace dies of.</summary>
    private const int Kill = 9;

    /// <summary>The code a Windows tree kill or its fallback gives every process it ends, upstream's own.</summary>
    private const int WindowsForcedExitCode = 1;

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Place_The_Server_In_A_Session_And_Group_Of_Its_Own(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();
        var server = await OpenCodeServer.StartAsync(LadderTree.Options(output, Grace), cancellationToken);
        try
        {
            if (OperatingSystem.IsWindows())
            {
                // Windows has no sessions to lead: the placement is the launcher's job.
                using var root = Process.GetProcessById(server.ProcessId);
                var job = KillOnCloseJob.ForCurrentProcess.Current;
                await Assert.That(job).IsNotNull();
                await Assert.That(WindowsProcessProbe.IsInJob(root.SafeHandle, job!)).IsTrue();
                Console.WriteLine("branch: Windows — pid " + server.ProcessId.ToString(CultureInfo.InvariantCulture) + " is in the launcher's kill-on-close job");
                return;
            }

            var pid = server.ProcessId;
            var placement = (Session: PosixProcessProbe.SessionOf(pid), Group: PosixProcessProbe.GroupOf(pid));
            var host = (Session: PosixProcessProbe.SessionOf(0), Group: PosixProcessProbe.GroupOf(0));

            // The positive control: a child Process starts stays in the host's session and group, so
            // the probe tells the two placements apart.
            var control = await ProcessControlPlacementAsync(cancellationToken);

            await Assert.That(placement.Session).IsEqualTo(pid);
            await Assert.That(placement.Group).IsEqualTo(pid);
            await Assert.That(host.Session).IsNotEqualTo(pid);
            await Assert.That(control.Session).IsEqualTo(host.Session);
            await Assert.That(control.Group).IsEqualTo(host.Group);
            Console.WriteLine(string.Format(
                CultureInfo.InvariantCulture,
                "branch: POSIX — server pid {0} leads session {1} and group {2}; the host is in session {3}, as a Process child is ({4})",
                pid,
                placement.Session,
                placement.Group,
                host.Session,
                control.Session));
        }
        finally
        {
            await server.DisposeAsync();
            await AssertEveryReportedProcessEndedAsync(output);
        }
    }

    /// <summary>
    /// The normal close needs no grace: on Linux and macOS <c>SIGTERM</c> ends the root and its
    /// group, and a child in a session of its own keeps running; on Windows one tree kill ends all
    /// three at once. The grace is far longer than any close takes, so the rung that ended the tree
    /// is what the test reads: no <c>SIGKILL</c> was sent, or the tree kill ran once. A close that
    /// waited the grace would have taken all of it by the launcher's own timer.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DisposeAsync_Should_End_The_Group_And_Leave_A_Child_In_A_Session_Of_Its_Own(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();
        var signals = RecordingSignals();
        var treeKill = new RecordingTreeKill();
        var server = await OpenCodeServer.StartWithSeamsAsync(
            LadderTree.Options(output, UnwaitedGrace, "group-child", "detached"),
            LauncherSeams.ForCurrentProcess() with { Signals = signals, TreeKill = treeKill },
            cancellationToken);
        try
        {
            var groupChild = LadderTree.Pid(output, LadderTree.GroupChild);
            var detached = LadderTree.Pid(output, LadderTree.Detached);
            var disposal = Stopwatch.StartNew();
            await server.DisposeAsync();
            var elapsed = disposal.Elapsed;
            var exit = await ChildExitObservation.WithinAsync(server, ObservationBound);
            var groupChildEnded = await ProcessObservation.ObserveExitWithinAsync(groupChild, ObservationBound, cancellationToken);

            await Assert.That(groupChildEnded).IsTrue();

            // The grace was not waited: a close that waits it takes all of it by its own timer.
            await Assert.That(elapsed).IsLessThan(UnwaitedGrace - TimerSlack);
            if (OperatingSystem.IsWindows())
            {
                // Upstream's Windows close: one tree kill at once, which reaches every live
                // descendant by parent pid, the detached child included, and no second rung runs.
                var detachedEnded = await ProcessObservation.ObserveExitWithinAsync(detached, ObservationBound, cancellationToken);
                await Assert.That(exit.ExitCode).IsEqualTo(WindowsForcedExitCode);
                await Assert.That(detachedEnded).IsTrue();
                await Assert.That(treeKill.Runs.Count).IsEqualTo(1);
                Console.WriteLine("branch: Windows — the tree kill ended the root (" + exit.Describe() + ") and both children after " + elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms");
                return;
            }

            await Assert.That(exit.Signal).IsEqualTo(Terminate);
            await Assert.That(ProcessObservation.IsRunning(detached)).IsTrue();
            _ = signals.Received(1).SignalGroup(server.ProcessId, ProcessSignal.Terminate);
            _ = signals.DidNotReceive().SignalGroup(Arg.Any<int>(), ProcessSignal.Kill);
            Console.WriteLine("branch: POSIX — the root " + exit.Describe() + ", its group child ended, and the detached child survived, without SIGKILL, after " + elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms");
        }
        finally
        {
            await AssertEveryReportedProcessEndedAsync(output);
        }
    }

    /// <summary>
    /// A root that outlives the grace gets the forced rung. On Linux and macOS the stand-in ignores
    /// <c>SIGTERM</c>, so <c>SIGKILL</c> ends it once the grace expired. On Windows the first tree
    /// kill reports success but ends nothing, the shape of a kill the root survived; the same kill
    /// runs again once the grace expired, and that one ends it.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DisposeAsync_Should_Kill_A_Root_That_Outlives_The_Grace(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();
        var treeKill = new RecordingTreeKill(idleRuns: 1);
        var server = await OpenCodeServer.StartWithSeamsAsync(
            LadderTree.Options(output, ShortGrace, "root-ignores-term"),
            LauncherSeams.ForCurrentProcess() with { TreeKill = treeKill },
            cancellationToken);
        try
        {
            treeKill.Restart();
            var disposal = Stopwatch.StartNew();
            await server.DisposeAsync();
            var elapsed = disposal.Elapsed;
            var exit = await ChildExitObservation.WithinAsync(server, ObservationBound);

            await Assert.That(elapsed).IsGreaterThanOrEqualTo(ShortGrace - TimerSlack);
            await Assert.That(elapsed).IsLessThan(ShortGrace + PosixServerLadder.ForcedExitTimeout);
            await Assert.That(ProcessObservation.IsRunning(server.ProcessId)).IsFalse();
            if (OperatingSystem.IsWindows())
            {
                var runs = treeKill.Runs;
                await Assert.That(runs.Count).IsEqualTo(2);
                await Assert.That(runs[1] - runs[0]).IsGreaterThanOrEqualTo(ShortGrace - TimerSlack);
                await Assert.That(exit.ExitCode).IsEqualTo(WindowsForcedExitCode);
            }
            else
            {
                await Assert.That(exit.Signal).IsEqualTo(Kill);
            }

            Console.WriteLine("branch: " + Platform() + " — after " + elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms the root " + exit.Describe());
        }
        finally
        {
            await AssertEveryReportedProcessEndedAsync(output);
        }
    }

    /// <summary>
    /// The root ends on <c>SIGTERM</c>, but a child in its group ignores it: within the grace the
    /// ladder waits for the root and then for the group, and the member left at the grace's end gets
    /// <c>SIGKILL</c>, as upstream's wait for its output pipes gets it there. Windows has no signal
    /// to ignore: the tree kill ends the root and the member together, at once.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DisposeAsync_Should_Kill_A_Group_Member_That_Outlives_The_Grace_After_The_Root_Exited(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();

        // On Windows a grace far longer than the bound below shows that none of it is waited.
        var server = await OpenCodeServer.StartAsync(
            LadderTree.Options(output, OperatingSystem.IsWindows() ? UnwaitedGrace : ShortGrace, "group-child-ignores-term"), cancellationToken);
        try
        {
            var groupChild = LadderTree.Pid(output, LadderTree.GroupChild);
            var disposal = Stopwatch.StartNew();
            await server.DisposeAsync();
            var elapsed = disposal.Elapsed;
            var exit = await ChildExitObservation.WithinAsync(server, ObservationBound);
            var groupChildEnded = await ProcessObservation.ObserveExitWithinAsync(groupChild, ObservationBound, cancellationToken);

            await Assert.That(groupChildEnded).IsTrue();
            if (OperatingSystem.IsWindows())
            {
                await Assert.That(elapsed).IsLessThan(ObservationBound);
                await Assert.That(exit.ExitCode).IsEqualTo(WindowsForcedExitCode);
            }
            else
            {
                await Assert.That(elapsed).IsGreaterThanOrEqualTo(ShortGrace - TimerSlack);
                await Assert.That(exit.Signal).IsEqualTo(Terminate);
            }

            Console.WriteLine("branch: " + Platform() + " — the root " + exit.Describe() + "; the group child that ignores SIGTERM was ended after " + elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms");
        }
        finally
        {
            await AssertEveryReportedProcessEndedAsync(output);
        }
    }

    /// <summary>
    /// A root that already exited on its own: on Linux and macOS, with a failure code its group's
    /// survivors are ended as a live root's group is; with code 0 the group is left alone, as
    /// upstream leaves it. On Windows the launcher runs no tree kill on an exited root, whatever its
    /// code: <c>taskkill</c> finds only live processes, so it would reach none of the root's
    /// descendants.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    [Arguments(3)]
    [Arguments(0)]
    public async Task DisposeAsync_Should_End_The_Survivors_Of_A_Root_That_Exited_Only_After_A_Failure(int code, CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();
        var treeKill = new RecordingTreeKill();
        var server = await OpenCodeServer.StartWithSeamsAsync(
            LadderTree.Options(output, Grace, "group-child", "exit-after-ready=" + code.ToString(CultureInfo.InvariantCulture)),
            LauncherSeams.ForCurrentProcess() with { TreeKill = treeKill },
            cancellationToken);
        try
        {
            var groupChild = LadderTree.Pid(output, LadderTree.GroupChild);
            var exit = await ChildExitObservation.WithinAsync(server, ObservationBound);
            await server.DisposeAsync();
            var survivorEnded = await ProcessObservation.ObserveExitWithinAsync(groupChild, ObservationBound, cancellationToken);

            await Assert.That(exit.ExitCode).IsEqualTo(code);
            if (OperatingSystem.IsWindows())
            {
                // Bun's own job ends a non-detached child with its parent, so whether the child
                // survives says nothing about the launcher here; the tree kill that never ran does.
                await Assert.That(treeKill.Runs).IsEmpty();
            }
            else
            {
                await Assert.That(survivorEnded).IsEqualTo(code != 0);
            }

            Console.WriteLine("branch: " + Platform() + " — the root exited with code " + code.ToString(CultureInfo.InvariantCulture) + "; its group child " + (survivorEnded ? "was ended" : "was left running"));
        }
        finally
        {
            await AssertEveryReportedProcessEndedAsync(output);
        }
    }

    /// <summary>
    /// The stand-in closes its stdout instead of reporting readiness and keeps running: the close
    /// itself fails the start, and the root is ended. The readiness timeout stays at its default,
    /// far beyond any start, so a launcher that waited for an exit or for the timeout instead would
    /// fail the start only at that timeout, and the failure would name the timeout, never the close.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Fail_Promptly_When_The_Server_Closes_Stdout_And_Keeps_Running(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();
        var options = LadderTree.Options(output, Grace, "close-stdout");
        if (OperatingSystem.IsWindows())
        {
            // A Bun stand-in cannot close its stdout on Windows: its runtime keeps a handle of its
            // own on it. The service fixture's stand-in closes the handle itself.
            var fixture = new ServiceFixtureCommand(new RealFileSystem()).Resolve();
            options.Command = [fixture[0], fixture[1], "close-stdout"];
        }

        try
        {
            var start = Stopwatch.StartNew();
            var failure = await Assert.That(async () => await OpenCodeServer.StartAsync(options, cancellationToken)).Throws<OpenCodeServerException>();
            var elapsed = start.Elapsed;
            var rootEnded = await ProcessObservation.ObserveExitWithinAsync(LadderTree.Pid(output, LadderTree.Root), ObservationBound, cancellationToken);

            await Assert.That(rootEnded).IsTrue();
            await Assert.That(failure!.Message).Contains("closed its standard output before reporting readiness");
            Console.WriteLine("branch: " + Platform() + " — failed after " + elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms: " + failure.Message);
        }
        finally
        {
            await AssertEveryReportedProcessEndedAsync(output);
        }
    }

    /// <summary>
    /// The root exits before readiness while a detached descendant keeps its stdout open, so no
    /// end-of-stream comes: the exit itself fails the start, after one bounded drain. The readiness
    /// timeout stays at its default, far beyond any start, so a launcher that waited for the
    /// end-of-stream instead would fail the start only at that timeout, and the failure would name
    /// the timeout, never the exit.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Report_An_Early_Exit_Promptly_While_A_Descendant_Holds_Stdout(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();
        var options = LadderTree.Options(output, Grace, "hold-stdout", "exit-before-ready=7");
        try
        {
            var start = Stopwatch.StartNew();
            var failure = await Assert.That(async () => await OpenCodeServer.StartAsync(options, cancellationToken)).Throws<OpenCodeServerException>();
            var elapsed = start.Elapsed;

            await Assert.That(failure!.Message).Contains("exited with code 7 before reporting readiness");
            Console.WriteLine("branch: " + Platform() + " — failed after " + elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms: " + failure.Message);
        }
        finally
        {
            await AssertEveryReportedProcessEndedAsync(output);
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Name_The_Signal_That_Ended_The_Server_Before_Readiness(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();
        try
        {
            var failure = await Assert.That(async () => await OpenCodeServer.StartAsync(
                LadderTree.Options(output, Grace, "kill-self-before-ready"), cancellationToken)).Throws<OpenCodeServerException>();

            await Assert.That(failure!.Message).Contains("before reporting readiness");
            if (!OperatingSystem.IsWindows())
            {
                await Assert.That(failure.Message).Contains("terminated on signal 9");
            }

            Console.WriteLine("branch: " + Platform() + " — " + failure.Message);
        }
        finally
        {
            await AssertEveryReportedProcessEndedAsync(output);
        }
    }

    /// <summary>
    /// The readiness line and the exit arrive together: the line was written first, so it is in the
    /// pipe whichever the launcher sees first, and the first line wins. Several rounds, because the
    /// order the two arrive in is the race under test.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Succeed_When_The_Server_Exits_Right_After_Readiness(CancellationToken cancellationToken)
    {
        const int rounds = 5;
        var outcomes = new List<string>();
        for (var round = 0; round < rounds; round++)
        {
            var output = new OpenCodeServerOutput();
            try
            {
                var server = await OpenCodeServer.StartAsync(LadderTree.Options(output, Grace, "exit-at-ready=0"), cancellationToken);
                await server.DisposeAsync();
                outcomes.Add("started");
            }
            finally
            {
                await AssertEveryReportedProcessEndedAsync(output);
            }
        }

        await Assert.That(outcomes).All().Satisfy(static outcome => outcome.IsEqualTo("started"));
        Console.WriteLine("branch: " + Platform() + " — " + string.Join(" | ", outcomes));
    }

    /// <summary>
    /// With <c>SIGCHLD</c> ignored the kernel reaps children itself, and a blocked wait for one could
    /// sleep until every child of the host is gone. The launcher reads the disposition at start and
    /// probes for the exit instead, and the ladder still ends the server. The seam reports the
    /// ignore and counts the blocking waits, which shows that the start hands what it read to the
    /// watch; the owner tests run a host that really ignores <c>SIGCHLD</c>, where no wait can be
    /// counted.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DisposeAsync_Should_Probe_For_The_Exit_When_The_Kernel_Reaps_Children_Itself(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();
        var exits = new ProbeOnlyChildExit();
        var server = await OpenCodeServer.StartWithSeamsAsync(
            LadderTree.Options(output, Grace, "group-child"),
            LauncherSeams.ForCurrentProcess() with { Exits = exits },
            cancellationToken);
        try
        {
            var groupChild = LadderTree.Pid(output, LadderTree.GroupChild);
            await server.DisposeAsync();
            var exit = await ChildExitObservation.WithinAsync(server, ObservationBound);
            var groupChildEnded = await ProcessObservation.ObserveExitWithinAsync(groupChild, ObservationBound, cancellationToken);

            await Assert.That(ProcessObservation.IsRunning(server.ProcessId)).IsFalse();
            await Assert.That(groupChildEnded).IsTrue();
            await Assert.That(exits.Waits).IsEqualTo(0);
            Console.WriteLine("branch: " + Platform() + " — the root " + exit.Describe() + " without a blocking wait");
        }
        finally
        {
            await AssertEveryReportedProcessEndedAsync(output);
        }
    }

    private static string Platform() => OperatingSystem.IsWindows() ? "Windows" : "POSIX";

    /// <summary>The shipped group signal behind a substitute that records every call it forwards.</summary>
    private static IProcessGroupSignal RecordingSignals()
    {
        var real = new ProcessGroupSignal();
        var recording = Substitute.For<IProcessGroupSignal>();
        _ = recording.SignalGroup(Arg.Any<int>(), Arg.Any<ProcessSignal>()).Returns(call => real.SignalGroup(call.Arg<int>(), call.Arg<ProcessSignal>()));
        _ = recording.SignalProcess(Arg.Any<int>(), Arg.Any<ProcessSignal>()).Returns(call => real.SignalProcess(call.Arg<int>(), call.Arg<ProcessSignal>()));
        _ = recording.ProbeGroup(Arg.Any<int>()).Returns(call => real.ProbeGroup(call.Arg<int>()));
        _ = recording.ProbeProcess(Arg.Any<int>()).Returns(call => real.ProbeProcess(call.Arg<int>()));
        return recording;
    }

    private static async Task AssertEveryReportedProcessEndedAsync(OpenCodeServerOutput output)
    {
        var survivors = await LadderTree.EndEveryReportedProcessAsync(output);
        await Assert.That(survivors).IsEmpty();
    }

    /// <summary>Starts a child the way <c>Process</c> does and reads its session and group before ending it.</summary>
    private static async Task<(int Session, int Group)> ProcessControlPlacementAsync(CancellationToken cancellationToken)
    {
        using var control = Process.Start(new ProcessStartInfo
        {
            FileName = "/bin/sh",
            Arguments = "-c \"sleep 60\"",
            UseShellExecute = false,
        }) ?? throw new InvalidOperationException("The control child did not start.");
        try
        {
            return (PosixProcessProbe.SessionOf(control.Id), PosixProcessProbe.GroupOf(control.Id));
        }
        finally
        {
            control.Kill();
            _ = await ProcessObservation.ObserveExitWithinAsync(control, ObservationBound, cancellationToken);
        }
    }
}
