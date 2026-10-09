using System.Diagnostics;
using System.Globalization;
using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Posix.Abstractions;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Internal.Windows.Abstractions;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests;

[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class OpenCodeServerLifecycleTests
{
    private static readonly RealFileSystem FileSystem = new();

    /// <summary>How long the test's own cleanup waits for the stdout holder it ended.</summary>
    private static readonly TimeSpan HolderExitBound = TimeSpan.FromSeconds(10);

    /// <summary>How long a test waits for a child it observes to write, or to exit after it was ended.</summary>
    private static readonly TimeSpan ChildObservationBound = TimeSpan.FromSeconds(10);

    private static async Task<(OpenCodeServer Server, TestRunRoot Root)> StartPinnedAsync(
        TimeSpan? gracefulShutdownTimeout = null,
        CancellationToken cancellationToken = default)
    {
        var runRoot = new TestRunRoot(FileSystem);
        return (await new PinnedServerLaunch(FileSystem).StartAsync(runRoot, null, gracefulShutdownTimeout, cancellationToken), runRoot);
    }

    [Test]
    [Timeout(240_000)]
    public async Task StartAsync_Should_Report_Readiness_And_Answer_Health(CancellationToken cancellationToken)
    {
        var (server, runRoot) = await StartPinnedAsync(cancellationToken: cancellationToken);
        using var _ = runRoot;
        await using var __ = server;

        await Assert.That(server.Endpoint.IsLoopback).IsTrue();
        await Assert.That(server.Endpoint.Port).IsNotEqualTo(0);
        await Assert.That(server.OwnsProcess).IsTrue();

        using var client = server.CreateClient();
        var health = await client.Server.GetInfoAsync(cancellationToken: cancellationToken);

        await Assert.That(health.Status).IsEqualTo(200);
        // Process truth: the server answering health is the exact child this start owns. bun
        // runs the entry in-process, so the reported pid is the spawned pid. If a platform leg
        // ever disproves this, record the deviation — do not soften the assertion silently.
        await Assert.That(health.ServerInfo.Pid).IsEqualTo(server.ProcessId);
    }

    /// <summary>
    /// The normal close of the pinned server, upstream's own on each platform. On Linux and macOS it
    /// is <c>SIGTERM</c> to the server's process group, which upstream's server answers the way its
    /// own runtime answers an interrupt: it shuts down and exits with code 130. On Windows it is the
    /// forced tree kill at once (<c>taskkill /T /F</c>), as upstream ends its server there: the
    /// server exits with code 1.
    /// </summary>
    [Test]
    [Timeout(240_000)]
    public async Task DisposeAsync_Should_End_The_Server_Gracefully(CancellationToken cancellationToken)
    {
        var (server, runRoot) = await StartPinnedAsync(cancellationToken: cancellationToken);
        using var _ = runRoot;
        var processId = server.ProcessId;

        await server.DisposeAsync();
        // A second disposal is a no-op by contract; this is the idempotence proof, not a stray line.
        await server.DisposeAsync();

        var exit = await ChildExitObservation.WithinAsync(server, ChildObservationBound);
        await Assert.That(ProcessObservation.IsRunning(processId)).IsFalse();
        await Assert.That(exit.ExitCode).IsEqualTo(OperatingSystem.IsWindows() ? 1 : 130).Because(exit.Describe());
        Console.WriteLine("branch: " + (OperatingSystem.IsWindows() ? "Windows tree kill" : "POSIX group SIGTERM") + " — the server " + exit.Describe());
    }

    [Test]
    [Timeout(240_000)]
    public async Task DisposeAsync_Should_End_The_Server_Through_The_Forced_Kill_Escalation(CancellationToken cancellationToken)
    {
        // Zero grace: the wait is already expired when disposal reaches it, so the forced
        // tree-kill path executes deterministically (net472 exercises the taskkill arm).
        var (server, runRoot) = await StartPinnedAsync(
            gracefulShutdownTimeout: TimeSpan.Zero, cancellationToken: cancellationToken);
        using var _ = runRoot;
        var processId = server.ProcessId;

        await server.DisposeAsync();

        await Assert.That(ProcessObservation.IsRunning(processId)).IsFalse();
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Refuse_A_Server_That_Exits_Before_Readiness(CancellationToken cancellationToken)
    {
        var failure = await Assert.That(async () => await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions
            {
                Command = ["bun", "-e", "console.error('boom'); process.exit(7)"],
            },
            cancellationToken)).Throws<OpenCodeServerException>();

        await Assert.That(failure!.Message).Contains("exited with code 7");
        await Assert.That(failure.Message).Contains("boom");
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Refuse_A_Server_That_Never_Reports_Readiness(CancellationToken cancellationToken)
    {
        await using var scenario = ServerStartupTreeScenario.CreateSilent(
            TimeSpan.FromSeconds(15));
        await scenario.StartAndObserveAsync(cancellationToken);

        var failure = await Assert.That(
            async () => _ = await scenario.WaitForStartupAsync(cancellationToken)).Throws<OpenCodeServerException>();

        await Assert.That(failure!.Message).Contains("did not report readiness");
        await AssertFailedStartEndedTheTreeAsync(scenario, cancellationToken);
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Refuse_A_Non_Contract_First_Line(CancellationToken cancellationToken)
    {
        await using var scenario = ServerStartupTreeScenario.CreateInvalidLine();
        await scenario.StartAndObserveAsync(cancellationToken);

        var failure = await Assert.That(
            async () => _ = await scenario.WaitForStartupAsync(cancellationToken)).Throws<OpenCodeServerException>();

        await Assert.That(failure!.Message).Contains("readiness contract");
        await AssertFailedStartEndedTheTreeAsync(scenario, cancellationToken);
    }

    /// <summary>
    /// The accepted completion contract after a failed start. The direct child's exit is
    /// immediate: the startup result is returned only after it. The grandchild is bounded
    /// descendant termination evidence, both leases still held (disposal releases them, after
    /// this): a tree kill is asynchronous, so it is observed to terminate inside its own bound
    /// rather than asserted gone at that instant.
    /// </summary>
    private static async Task AssertFailedStartEndedTheTreeAsync(
        ServerStartupTreeScenario scenario,
        CancellationToken cancellationToken)
    {
        var rootExited = scenario.RootProcess.HasExited;
        await Assert.That(rootExited).IsTrue().Because(rootExited ? string.Empty : await scenario.DescribeProcessesAsync());

        var descendantTerminated = await scenario.ObserveDescendantTerminationAsync(cancellationToken);
        await Assert.That(descendantTerminated).IsTrue().Because(scenario.DescendantEvidence);
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartupTreeScenario_Should_Clean_Observed_Tree_When_Handshake_Fails(
        CancellationToken cancellationToken)
    {
        await using var scenario = ServerStartupTreeScenario.CreateHandshakeFailure();
        InvalidOperationException? cleanupFailure = null;
        try
        {
            var observationFailure = await Assert.That(
                async () => await scenario.StartAndObserveAsync(cancellationToken))
                .Throws<InvalidOperationException>();
            _ = await Assert.That(
                async () => _ = await scenario.WaitForStartupAsync(cancellationToken))
                .Throws<OpenCodeServerException>();

            await Assert.That(scenario.RootProcess.HasExited).IsTrue();
            await Assert.That(scenario.ChildProcess.HasExited).IsTrue();

            await Assert.That(observationFailure!.Message)
                .Contains("intentionally rejected for cleanup verification");
        }
        finally
        {
            try
            {
                await scenario.DisposeAsync();
            }
            catch (InvalidOperationException exception)
            {
                cleanupFailure = exception;
            }
        }

        await Assert.That(cleanupFailure).IsNotNull();
        await Assert.That(cleanupFailure!.Message)
            .Contains("intentionally rejected for cleanup verification");
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartupTreeScenario_Should_Release_Each_Owned_Lease_Independently(
        CancellationToken cancellationToken)
    {
        await using var scenario = ServerStartupTreeScenario.CreateSilent(TimeSpan.FromMinutes(2));
        await scenario.StartAndObserveAsync(cancellationToken);
        await Assert.That(scenario.RootProcess.HasExited).IsFalse();
        await Assert.That(scenario.ChildProcess.HasExited).IsFalse();

        // Exercise cooperative cleanup while startup is still waiting. This is fixture evidence,
        // not launcher-reaping evidence: the latter tests keep both leases open until assertions.
        scenario.ReleaseChildLease();
        await scenario.ChildProcess.WaitForExitAsync(cancellationToken);
        await Assert.That(scenario.ChildProcess.HasExited).IsTrue();
        await Assert.That(scenario.RootProcess.HasExited).IsFalse();

        scenario.ReleaseRootLease();
        _ = await Assert.That(async () => _ = await scenario.WaitForStartupAsync(cancellationToken))
            .Throws<OpenCodeServerException>();
        await Assert.That(scenario.RootProcess.HasExited).IsTrue();
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Retain_Both_Streams_For_A_Pull_Snapshot(CancellationToken cancellationToken)
    {
        const string readyLine = "{\"url\":\"http://127.0.0.1:1\"}";
        var output = new OpenCodeServerOutput();
        var server = await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions
            {
                // A stand-in child: one stderr line, then the readiness contract, an empty stdout
                // line, and a later stdout line in one write, held open until the launcher ends it.
                // Nothing is written after readiness, so a SIGTERM sent on disposal cannot pre-empt
                // a line the snapshot expects.
                Command =
                [
                    "bun", "-e",
                    "console.error('warn-1'); process.stdout.write('" + readyLine + "' + '\\n\\nlater\\n'); setTimeout(() => {}, 120000)",
                ],
                GracefulShutdownTimeout = TimeSpan.Zero,
                Output = output,
            },
            cancellationToken);

        // Readiness parsing stayed independent of the collector: the same first line drove both.
        await Assert.That(server.Endpoint).IsEqualTo(new Uri("http://127.0.0.1:1"));

        await server.DisposeAsync();

        var snapshot = output.GetSnapshot();
        await Assert.That(snapshot.StandardOutput).IsEquivalentTo([readyLine, "", "later"], CollectionOrdering.Matching);
        await Assert.That(snapshot.StandardError).IsEquivalentTo(["warn-1"], CollectionOrdering.Matching);
        await Assert.That(snapshot.StandardOutputTruncated).IsFalse();
        await Assert.That(snapshot.StandardErrorTruncated).IsFalse();
    }

    /// <summary>
    /// What the collector holds of a close. The ladder ends the server before stdin closes, on every
    /// platform: <c>SIGTERM</c> on Linux and macOS, the forced tree kill on Windows, as upstream ends
    /// it. A child that would write its last lines only on stdin's end-of-stream never writes them,
    /// so the final snapshot holds what it wrote while it ran, and the collection is closed.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DisposeAsync_Should_Finalize_The_Collector_After_The_Ladder_Ended_The_Server(CancellationToken cancellationToken)
    {
        const string readyLine = "{\"url\":\"http://127.0.0.1:1\"}";
        var output = new OpenCodeServerOutput();
        var server = await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions
            {
                // The child writes its last lines only once the stdin lease is released.
                Command =
                [
                    "bun", "-e",
                    "console.log('" + readyLine + "'); process.stdin.resume(); process.stdin.on('end', () => { console.log('FINAL-STDOUT'); console.error('FINAL-STDERR'); });",
                ],
                Output = output,
            },
            cancellationToken);

        await server.DisposeAsync();

        output.AppendStandardOutput("after-disposal");
        var snapshot = output.GetSnapshot();
        await Assert.That(snapshot.StandardOutput).IsEquivalentTo([readyLine], CollectionOrdering.Matching);
        await Assert.That(snapshot.StandardError).IsEmpty();
        Console.WriteLine("branch: " + (OperatingSystem.IsWindows() ? "Windows" : "POSIX") + " — the ladder ended the child before stdin closed, so it wrote no shutdown lines: " + string.Join(" | ", snapshot.StandardOutput));
    }

    [Test]
    [Timeout(120_000)]
    public async Task DisposeAsync_Should_Finalize_The_Collector_For_A_Child_That_Already_Exited(CancellationToken cancellationToken)
    {
        const string readyLine = "{\"url\":\"http://127.0.0.1:1\"}";
        var output = new OpenCodeServerOutput();
        var server = await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions
            {
                // Ready, one stderr line, then a self-initiated exit shortly after: disposal
                // finds no child to end and still finalizes what the readers deliver.
                Command =
                [
                    "bun", "-e",
                    "console.log('" + readyLine + "'); console.error('bye'); setTimeout(() => process.exit(0), 500);",
                ],
                Output = output,
            },
            cancellationToken);
        await ProcessObservation.WaitForExitAsync(server.ProcessId, cancellationToken);

        await server.DisposeAsync();

        var snapshot = output.GetSnapshot();
        await Assert.That(snapshot.StandardOutput).IsEquivalentTo([readyLine], CollectionOrdering.Matching);
        await Assert.That(snapshot.StandardError).IsEquivalentTo(["bye"], CollectionOrdering.Matching);
    }

    /// <summary>
    /// Disposal releases the output readers with no collector attached too: they run as pending
    /// asynchronous reads while the child lives, on every platform, and none of them outlives the
    /// owning handle.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DisposeAsync_Should_End_The_Output_Readers_Without_A_Collector(CancellationToken cancellationToken)
    {
        const string readyLine = "{\"url\":\"http://127.0.0.1:1\"}";
        var server = await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions
            {
                Command = ["bun", "-e", "console.log('" + readyLine + "'); setTimeout(() => {}, 120000)"],
                GracefulShutdownTimeout = TimeSpan.Zero,
            },
            cancellationToken);
        var runningWhileLive = !server.OutputReadersEnded.IsCompleted;

        await server.DisposeAsync();

        await Assert.That(runningWhileLive).IsTrue();
        await Assert.That(server.OutputReadersEnded.IsCompleted).IsTrue();
    }

    /// <summary>
    /// A forced end the platform does not complete is a result disposal continues from: every
    /// release step after it runs, the reader release and the collector's completion included. On
    /// Windows the tree kill fails without ending anything: the ladder falls back to
    /// <c>TerminateProcess</c> on the root with code 1, upstream's fallback, without waiting out the
    /// grace, and the tree kill runs once. On Linux and macOS every signal is refused, the group and
    /// the pid alike: nothing is escalated, disposal returns without waiting out the grace, and the
    /// child is left running for the test to end.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DisposeAsync_Should_Release_The_Readers_When_The_Forced_End_Fails(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await AssertFailedTreeKillReleasesAsync(cancellationToken);
            return;
        }

        const string readyLine = "{\"url\":\"http://127.0.0.1:1\"}";
        var refusing = RefusingSignals();
        var output = new OpenCodeServerOutput();
        var server = await OpenCodeServer.StartWithSeamsAsync(
            new OpenCodeServerOptions
            {
                Command = ["bun", "-e", "console.log('" + readyLine + "'); setTimeout(() => {}, 120000);"],
                GracefulShutdownTimeout = TimeSpan.FromSeconds(30),
                Output = output,
            },
            LauncherSeams.ForCurrentProcess() with { Signals = refusing },
            cancellationToken);
        var processId = server.ProcessId;
        try
        {
            var disposal = Stopwatch.StartNew();
            await server.DisposeAsync();
            var elapsed = disposal.Elapsed;

            _ = refusing.Received(1).SignalGroup(processId, ProcessSignal.Terminate);
            _ = refusing.Received(1).SignalProcess(processId, ProcessSignal.Terminate);
            _ = refusing.DidNotReceive().SignalGroup(Arg.Any<int>(), ProcessSignal.Kill);
            _ = refusing.DidNotReceive().SignalProcess(Arg.Any<int>(), ProcessSignal.Kill);
            await Assert.That(elapsed).IsLessThan(TimeSpan.FromSeconds(10));
            await Assert.That(server.OutputReadersEnded.IsCompleted).IsTrue();
            await Assert.That(ProcessObservation.IsRunning(processId)).IsTrue();
            output.AppendStandardOutput("after-disposal");
            await Assert.That(output.GetSnapshot().StandardOutput).IsEquivalentTo([readyLine], CollectionOrdering.Matching);
            Console.WriteLine("branch: POSIX — both SIGTERM targets refused, no SIGKILL, disposal returned in " + elapsed.TotalMilliseconds.ToString("F0", CultureInfo.InvariantCulture) + " ms");
        }
        finally
        {
            _ = new ProcessGroupSignal().SignalGroup(processId, ProcessSignal.Kill);
            _ = await ChildExitObservation.WithinAsync(server, ChildObservationBound);
        }
    }

    /// <summary>
    /// A signal seam whose every signal is refused; the existence probes still reach the real
    /// kernel, so the exit watch and the group probe see the truth.
    /// </summary>
    private static IProcessGroupSignal RefusingSignals()
    {
        var real = new ProcessGroupSignal();
        var refusing = Substitute.For<IProcessGroupSignal>();
        _ = refusing.SignalGroup(Arg.Any<int>(), Arg.Any<ProcessSignal>()).Returns(SignalDelivery.Refused);
        _ = refusing.SignalProcess(Arg.Any<int>(), Arg.Any<ProcessSignal>()).Returns(SignalDelivery.Refused);
        _ = refusing.ProbeGroup(Arg.Any<int>()).Returns(call => real.ProbeGroup(call.Arg<int>()));
        _ = refusing.ProbeProcess(Arg.Any<int>()).Returns(call => real.ProbeProcess(call.Arg<int>()));
        return refusing;
    }

    private static async Task AssertFailedTreeKillReleasesAsync(CancellationToken cancellationToken)
    {
        const string readyLine = "{\"url\":\"http://127.0.0.1:1\"}";
        var failing = FailingTreeKill();
        var output = new OpenCodeServerOutput();
        var server = await OpenCodeServer.StartWithSeamsAsync(
            new OpenCodeServerOptions
            {
                Command = ["bun", "-e", "console.log('" + readyLine + "'); setTimeout(() => {}, 120000);"],
                GracefulShutdownTimeout = TimeSpan.FromSeconds(30),
                Output = output,
            },
            LauncherSeams.ForCurrentProcess() with { TreeKill = failing },
            cancellationToken);
        var processId = server.ProcessId;
        try
        {
            var disposal = Stopwatch.StartNew();
            await server.DisposeAsync();
            var elapsed = disposal.Elapsed;
            var exit = await ChildExitObservation.WithinAsync(server, ChildObservationBound);

            _ = await failing.Received(1).KillTreeAsync(processId, Arg.Any<TimeSpan>());
            await Assert.That(exit.ExitCode).IsEqualTo(1);
            await Assert.That(elapsed).IsLessThan(TimeSpan.FromSeconds(10));
            await Assert.That(server.OutputReadersEnded.IsCompleted).IsTrue();
            output.AppendStandardOutput("after-disposal");
            await Assert.That(output.GetSnapshot().StandardOutput).IsEquivalentTo([readyLine], CollectionOrdering.Matching);
            Console.WriteLine("branch: Windows — the failed tree kill of pid " + processId.ToString(CultureInfo.InvariantCulture) + " fell back to TerminateProcess: the root " + exit.Describe());
        }
        finally
        {
            ProcessObservation.KillIfRunning(processId);
        }
    }

    /// <summary>A tree kill that reports a failure and ends nothing, as a <c>taskkill</c> that could not start does.</summary>
    private static IWindowsTreeKill FailingTreeKill()
    {
        var failing = Substitute.For<IWindowsTreeKill>();
        _ = failing.KillTreeAsync(Arg.Any<int>(), Arg.Any<TimeSpan>()).Returns(Task.FromResult(TreeKillOutcome.Failed));
        return failing;
    }

    /// <summary>
    /// A failed start whose forced end the platform does not complete fails as the start failure it
    /// is, never as a teardown fault, and still releases everything: the stderr the drain collected
    /// is quoted, and the collector is closed. On Windows the tree kill fails, and the fallback ends
    /// the root with code 1. On Linux and macOS every signal is refused, so the child is left
    /// running and the test ends it by its group.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Report_The_Start_Failure_When_The_Forced_End_Fails(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await AssertFailedTreeKillReportsTheStartFailureAsync(cancellationToken);
            return;
        }

        var refusing = RefusingSignals();
        var output = new OpenCodeServerOutput();
        var processId = 0;
        try
        {
            var failure = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(
                new OpenCodeServerOptions
                {
                    Command = ["bun", "-e", "console.error('starting pid=' + process.pid); setTimeout(() => {}, 120000)"],
                    ReadinessTimeout = TimeSpan.FromSeconds(5),
                    GracefulShutdownTimeout = TimeSpan.FromSeconds(30),
                    Output = output,
                },
                LauncherSeams.ForCurrentProcess() with { Signals = refusing },
                cancellationToken)).Throws<OpenCodeServerException>();

            await Assert.That(failure!.Message).Contains("did not report readiness");
            await Assert.That(failure.Message).Contains("starting pid=");
            processId = StartingPid(output.GetSnapshot().StandardError);
            _ = refusing.Received(1).SignalGroup(processId, ProcessSignal.Terminate);
            _ = refusing.DidNotReceive().SignalGroup(Arg.Any<int>(), ProcessSignal.Kill);
            await Assert.That(ProcessObservation.IsRunning(processId)).IsTrue();

            output.AppendStandardError("after-failure");
            await Assert.That(output.GetSnapshot().StandardError).Count().IsEqualTo(1);
            Console.WriteLine("branch: POSIX — the refused SIGTERM left pid " + processId.ToString(CultureInfo.InvariantCulture) + " running; the start failure was reported");
        }
        finally
        {
            processId = processId is 0 ? TryStartingPid(output.GetSnapshot().StandardError) : processId;
            if (processId > 1)
            {
                _ = new ProcessGroupSignal().SignalGroup(processId, ProcessSignal.Kill);
                _ = await ProcessObservation.ObserveExitWithinAsync(processId, ChildObservationBound, CancellationToken.None);
            }
        }
    }

    private static int StartingPid(IReadOnlyList<string> standardError) =>
        TryStartingPid(standardError) is var pid and > 1
            ? pid
            : throw new InvalidOperationException("The stand-in reported no pid: " + string.Join(" | ", standardError));

    private static int TryStartingPid(IReadOnlyList<string> standardError)
    {
        const string prefix = "starting pid=";
        foreach (var line in standardError)
        {
            if (line.StartsWith(prefix, StringComparison.Ordinal) &&
                int.TryParse(line.AsSpan(prefix.Length), NumberStyles.Integer, CultureInfo.InvariantCulture, out var pid))
            {
                return pid;
            }
        }

        return 0;
    }

    private static async Task AssertFailedTreeKillReportsTheStartFailureAsync(CancellationToken cancellationToken)
    {
        var failing = FailingTreeKill();
        var output = new OpenCodeServerOutput();
        var processId = 0;
        try
        {
            var failure = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(
                new OpenCodeServerOptions
                {
                    Command = ["bun", "-e", "console.error('starting pid=' + process.pid); setTimeout(() => {}, 120000)"],
                    ReadinessTimeout = TimeSpan.FromSeconds(5),
                    Output = output,
                },
                LauncherSeams.ForCurrentProcess() with { TreeKill = failing },
                cancellationToken)).Throws<OpenCodeServerException>();

            await Assert.That(failure!.Message).Contains("did not report readiness");
            await Assert.That(failure.Message).Contains("starting pid=");
            processId = StartingPid(output.GetSnapshot().StandardError);
            _ = await failing.Received(1).KillTreeAsync(processId, Arg.Any<TimeSpan>());
            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(processId, ChildObservationBound, cancellationToken)).IsTrue();

            output.AppendStandardError("after-failure");
            await Assert.That(output.GetSnapshot().StandardError).Count().IsEqualTo(1);
            Console.WriteLine("branch: Windows — the failed tree kill fell back to TerminateProcess; the start failure was reported: " + failure.Message);
        }
        finally
        {
            processId = processId is 0 ? TryStartingPid(output.GetSnapshot().StandardError) : processId;
            if (processId > 0)
            {
                ProcessObservation.KillIfRunning(processId);
            }
        }
    }

    /// <summary>
    /// The normal close waits for the child's own exit, not for its output to reach end-of-stream:
    /// a descendant still holding stdout does not hold disposal open past the child's prompt exit
    /// (on the tree kill on Windows, on SIGTERM elsewhere), and the readers of the pipes it still holds
    /// are released all the same. The holder is the test's to end, by the pid it recorded.
    /// </summary>
    /// <remarks>
    /// The long grace keeps the bound far from both outcomes. A close that waits for end-of-stream
    /// runs out the whole grace and then the 10-second forced-exit wait, about 40 seconds; the
    /// prompt close takes well under a second. A 10-second bound leaves room for a loaded machine
    /// and still fails the waiting close by 30 seconds.
    /// </remarks>
    [Test]
    [Timeout(120_000)]
    public async Task DisposeAsync_Should_Return_Promptly_When_A_Descendant_Holds_Stdout(CancellationToken cancellationToken)
    {
        using var runRoot = new TestRunRoot(FileSystem);
        var pidFile = FileSystem.Path.Combine(runRoot.Path, "holder.pid");
        OpenCodeServer? server = null;
        int? holderId = null;
        var holderRunningAtClose = false;
        var holderEnded = false;
        var readersEndedAtClose = false;
        TimeSpan elapsed;
        try
        {
            server = await OpenCodeServer.StartAsync(
                new OpenCodeServerOptions
                {
                    Command = ["bun", "-e", new FixtureLoader().LoadText("Server.stdout-holder.js")],
                    Environment = new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["OPENCODE_SDK_TEST_HOLDER_PID_FILE"] = pidFile,
                    },
                    GracefulShutdownTimeout = TimeSpan.FromSeconds(30),
                },
                cancellationToken);
            holderId = await ReadProcessIdAsync(pidFile, cancellationToken);
            holderRunningAtClose = ProcessObservation.IsRunning(holderId.Value);
            var disposal = Stopwatch.StartNew();
            await server.DisposeAsync();
            elapsed = disposal.Elapsed;

            // The holder still holds the output pipes, so only the release can have ended the readers.
            readersEndedAtClose = server.OutputReadersEnded.IsCompleted;
        }
        finally
        {
            if (server is not null)
            {
                // A no-op after the measured disposal; it ends the child when the arrangement failed.
                await server.DisposeAsync();
            }

            // The fixture records the holder before it reports readiness, so a start or a pid read
            // that failed can still have left a holder running.
            holderId ??= await TryReadProcessIdAsync(pidFile);
            if (holderId is { } id)
            {
                ProcessObservation.KillIfRunning(id);
                holderEnded = await ProcessObservation.ObserveExitWithinAsync(id, HolderExitBound, CancellationToken.None);
            }
        }

        await Assert.That(holderRunningAtClose).IsTrue();
        await Assert.That(elapsed).IsLessThan(TimeSpan.FromSeconds(10));
        await Assert.That(readersEndedAtClose).IsTrue();
        await Assert.That(holderEnded).IsTrue();
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Leave_The_Collector_Readable_After_A_Failed_Start(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();

        var failure = await Assert.That(async () => await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions
            {
                Command = ["bun", "-e", "console.error('boom'); process.exit(7)"],
                Output = output,
            },
            cancellationToken)).Throws<OpenCodeServerException>();

        // The startup exception's own tail and the collector are independent witnesses.
        await Assert.That(failure!.Message).Contains("boom");
        var snapshot = output.GetSnapshot();
        await Assert.That(snapshot.StandardError).IsEquivalentTo(["boom"], CollectionOrdering.Matching);
        await Assert.That(snapshot.StandardOutput).IsEmpty();
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Refuse_A_Collector_An_Earlier_Start_Bound_Before_Spawning(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();
        _ = await Assert.That(async () => await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions
            {
                Command = ["bun", "-e", "process.exit(7)"],
                Output = output,
            },
            cancellationToken)).Throws<OpenCodeServerException>();

        // Refused as an options error, not as a spawn failure: the missing executable is never run.
        var refusal = await Assert.That(async () => await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions
            {
                Command = ["opencode-sdk-test-missing-executable"],
                Output = output,
            },
            cancellationToken)).Throws<ArgumentException>();

        await Assert.That(refusal!.Message).Contains("already bound");
    }

    [Test]
    [Timeout(120_000)]
    public async Task ProcessId_Should_Stay_Readable_After_Disposal(CancellationToken cancellationToken)
    {
        var server = await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions
            {
                Command = ["bun", "-e", "console.log('{\"url\":\"http://127.0.0.1:1\"}'); setTimeout(() => {}, 120000)"],
                GracefulShutdownTimeout = TimeSpan.Zero,
            },
            cancellationToken);
        var processId = server.ProcessId;

        await server.DisposeAsync();

        // Owners write their failure diagnostics after teardown, so the child's identity has to
        // outlive the process handle disposal released.
        await Assert.That(server.ProcessId).IsEqualTo(processId);
        await Assert.That(ProcessObservation.IsRunning(processId)).IsFalse();
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Refuse_A_Missing_Executable(CancellationToken cancellationToken)
    {
        var failure = await Assert.That(async () => await OpenCodeServer.StartAsync(
            new OpenCodeServerOptions
            {
                Command = ["opencode-sdk-test-missing-executable"],
            },
            cancellationToken)).Throws<OpenCodeServerException>();

        // Resolution refuses it before anything is spawned, so the message is the searched-PATH
        // report rather than a bare Win32 error the caller cannot act on.
        await Assert.That(failure!.Message).Contains("'opencode-sdk-test-missing-executable'");
        await Assert.That(failure.Message).Contains("was not found on PATH");
        await Assert.That(failure.Message).Contains("directories searched");
        await Assert.That(failure.Message).Contains("OpenCodeServerOptions.Command");
        await Assert.That(failure.Message).Contains("@opencode/cli");
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Surface_Caller_Cancellation(CancellationToken cancellationToken)
    {
        using var cancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        await using var scenario = ServerStartupTreeScenario.CreateSilent(
            TimeSpan.FromMinutes(2));
        await scenario.StartAndObserveAsync(cancellation.Token);

        await cancellation.CancelOnWorkerAsync();

        _ = await Assert.That(
            async () => _ = await scenario.WaitForStartupAsync(cancellationToken)).Throws<OperationCanceledException>();
        await AssertFailedStartEndedTheTreeAsync(scenario, cancellationToken);
    }

    private static async Task<int> ReadProcessIdAsync(string path, CancellationToken cancellationToken)
    {
#if NET
        var text = await FileSystem.File.ReadAllTextAsync(path, cancellationToken);
#else
        var text = await Task.FromResult(FileSystem.File.ReadAllText(path)).WaitAsync(cancellationToken);
#endif
        return int.Parse(text, CultureInfo.InvariantCulture);
    }

    /// <summary>Reads a recorded pid for cleanup; null when none was recorded.</summary>
    private static async Task<int?> TryReadProcessIdAsync(string path)
    {
        if (!FileSystem.File.Exists(path))
        {
            return null;
        }

#if NET
        var text = await FileSystem.File.ReadAllTextAsync(path, CancellationToken.None);
#else
        var text = await Task.FromResult(FileSystem.File.ReadAllText(path));
#endif
        return int.TryParse(text, NumberStyles.Integer, CultureInfo.InvariantCulture, out var id) ? id : null;
    }
}
