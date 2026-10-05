using System.Globalization;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Launcher;

/// <summary>
/// The POSIX child against real processes: a line whose reader delivers it only after disposal
/// ended the server, and the paths <c>OpenCodeServer</c> never takes on purpose: a readiness wait that
/// an unexpected failure leaves before any ladder ran, a line handler that throws, and a spawned
/// child the launcher cannot adopt. Windows has no POSIX spawn; each Windows arm asserts that a
/// start there still runs on <c>Process</c> and that disposal ends it. Every process a test starts
/// is ended by its pid before the test returns. Keyless
/// <c>[NotInParallel]</c>: the proofs ride wall-clock bounds.
/// </summary>
[NotInParallel]
public sealed class PosixServerChildTests
{
    private const string FailedReadiness = "the readiness wait failed";

    private const string IdleStandIn = "setInterval(() => {}, 1000);";

    private const string ReadyLine = "READY";

    private const string BodyLine = "BODY";

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private static readonly Action<string> IgnoreLine = static _ => { };

    /// <summary>How long a test waits for a process it observes to end.</summary>
    private static readonly TimeSpan ObservationBound = TimeSpan.FromSeconds(10);

    /// <summary>
    /// The server wrote a line right after its readiness line, but its reader delivers it only
    /// once the server has ended: the line handler holds it until the disposal ladder saw the root
    /// exit. The collection stays open through the ladder, and the drain after it waits for the
    /// reader, so the line is in the final snapshot.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task EndAsync_Should_Collect_A_Line_Its_Reader_Delivers_After_The_Server_Ended(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await AssertWindowsStartRunsOnProcessAsync(cancellationToken);
            return;
        }

        var output = new OpenCodeServerOutput();
        var readyLine = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deliverBody = new ManualResetEventSlim();
        var start = Start(
            new OpenCodeServerOptions { Command = ["/bin/sh", "-c", "echo " + ReadyLine + "; echo " + BodyLine + "; exec sleep 60"], GracefulShutdownTimeout = Grace },
            line =>
            {
                // The reader runs this handler, so holding it holds every later line of the pipe.
                if (!readyLine.TrySetResult(line))
                {
                    _ = bodyHeld.TrySetResult();
                    _ = deliverBody.Wait(ObservationBound, cancellationToken);
                }

                output.AppendStandardOutput(line);
            },
            output);
        PosixServerChild? child = null;
        try
        {
            child = await PosixServerChild.SpawnAsync(start, LauncherSeams.ForCurrentProcess());
            var outcome = await child.WaitForReadinessAsync(readyLine.Task, cancellationToken);
            await bodyHeld.Task.WaitAsync(ObservationBound, cancellationToken);
            var beforeDisposal = output.GetSnapshot().StandardOutput;
            var ending = child.EndAsync();
            var exit = await child.Exited.WaitAsync(ObservationBound, cancellationToken);
            deliverBody.Set();
            await ending.WaitAsync(ObservationBound, cancellationToken);

            await Assert.That(outcome.ReadyLine).IsEqualTo(ReadyLine);
            await Assert.That(beforeDisposal).IsEquivalentTo([ReadyLine], CollectionOrdering.Matching);
            await Assert.That(exit).IsEqualTo(new ChildExitStatus { Signal = 15 });
            await Assert.That(output.GetSnapshot().StandardOutput).IsEquivalentTo([ReadyLine, BodyLine], CollectionOrdering.Matching);
            Console.WriteLine("branch: POSIX — the root " + exit.Describe() + ", and the line its reader delivered afterwards reached the final snapshot");
        }
        finally
        {
            deliverBody.Set();
            if (child is not null)
            {
                await child.DisposeAsync();
                if (!child.Exited.IsCompleted)
                {
                    ProcessObservation.KillIfRunning(child.ProcessId);
                }
            }
        }
    }

    /// <summary>
    /// An unexpected failure leaves the readiness wait before the ladder ran: here the readiness
    /// task faults while the root runs. Disposal then runs the ladder before it releases anything,
    /// so the root and the member of its group end, and the watch and the readers end with them.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DisposeAsync_Should_End_The_Server_And_Its_Group_When_No_Ladder_Ran(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await AssertWindowsStartRunsOnProcessAsync(cancellationToken);
            return;
        }

        var output = new OpenCodeServerOutput();
        var start = Start(LadderTree.Options(output, Grace, LadderTree.GroupChild), output.AppendStandardOutput, output);
        PosixServerChild? child = null;
        try
        {
            child = await PosixServerChild.SpawnAsync(start, LauncherSeams.ForCurrentProcess());
            var groupChild = await GroupChildAsync(output, cancellationToken);
            var escaped = await Assert.That(async () => await child.WaitForReadinessAsync(
                    Task.FromException<string>(new InvalidOperationException(FailedReadiness)), cancellationToken))
                .Throws<InvalidOperationException>();
            var runningBeforeDisposal = ProcessObservation.IsRunning(groupChild) && ProcessObservation.IsRunning(child.ProcessId);
            await child.DisposeAsync();
            var exit = await child.Exited.WaitAsync(ObservationBound, cancellationToken);
            var groupChildEnded = await ProcessObservation.ObserveExitWithinAsync(groupChild, ObservationBound, cancellationToken);

            await Assert.That(escaped!.Message).IsEqualTo(FailedReadiness);
            await Assert.That(runningBeforeDisposal).IsTrue();
            await Assert.That(exit).IsEqualTo(new ChildExitStatus { Signal = 15 });
            await Assert.That(groupChildEnded).IsTrue();
            await Assert.That(child.ReadersEnded.IsCompleted).IsTrue();
            Console.WriteLine("branch: POSIX — the root " + exit.Describe() + ", and its group child " + groupChild.ToString(CultureInfo.InvariantCulture) + " was ended by disposal");
        }
        finally
        {
            if (child is not null)
            {
                await child.DisposeAsync();
            }

            await Assert.That(await LadderTree.EndEveryReportedProcessAsync(output)).IsEmpty();
        }
    }

    /// <summary>
    /// A stdout line handler that throws ends that reader, and the root then exits with a failure
    /// code while a member of its group runs on. The ended reader is an ended stream to the
    /// readiness wait, not an exception out of it: the start fails as an exit, the ladder ends the
    /// group, and disposal releases everything without raising the handler's failure.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task WaitForReadinessAsync_Should_End_The_Group_When_A_Line_Handler_Throws(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await AssertWindowsStartRunsOnProcessAsync(cancellationToken);
            return;
        }

        var output = new OpenCodeServerOutput();
        var start = Start(
            LadderTree.Options(output, Grace, LadderTree.GroupChild, "exit-at-ready=3"),
            static _ => throw new InvalidOperationException(FailedReadiness),
            output);
        PosixServerChild? child = null;
        try
        {
            child = await PosixServerChild.SpawnAsync(start, LauncherSeams.ForCurrentProcess());
            var groupChild = await GroupChildAsync(output, cancellationToken);
            var outcome = await child.WaitForReadinessAsync(NeverReadyAsync(cancellationToken), cancellationToken);
            var groupChildEnded = await ProcessObservation.ObserveExitWithinAsync(groupChild, ObservationBound, cancellationToken);
            await child.DisposeAsync();

            await Assert.That(outcome.ReadyLine).IsNull();
            await Assert.That(outcome.Exit).IsEqualTo(new ChildExitStatus { ExitCode = 3 });
            await Assert.That(groupChildEnded).IsTrue();
            await Assert.That(child.ReadersEnded.IsCompleted).IsTrue();
            Console.WriteLine("branch: POSIX — the root " + outcome.Exit!.Describe() + ", and its group child " + groupChild.ToString(CultureInfo.InvariantCulture) + " was ended by the failed start's ladder");
        }
        finally
        {
            if (child is not null)
            {
                await child.DisposeAsync();
            }

            await Assert.That(await LadderTree.EndEveryReportedProcessAsync(output)).IsEmpty();
        }
    }

    /// <summary>
    /// A spawned child the launcher cannot adopt is killed with its group and reaped by the failing
    /// start itself, inside the bound, with no watch to race it: once the start threw, its pid
    /// names no process at all, not even a zombie.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task SpawnAsync_Should_Kill_And_Reap_A_Child_It_Cannot_Adopt(CancellationToken cancellationToken)
    {
        if (OperatingSystem.IsWindows())
        {
            await AssertWindowsStartRunsOnProcessAsync(cancellationToken);
            return;
        }

        var spawn = new SpawnWithoutStandardOutput();
        var seams = LauncherSeams.ForCurrentProcess() with { Spawn = spawn };
        var start = Start(
            new OpenCodeServerOptions { Command = ["/bin/sh", "-c", "exec sleep 60"] },
            IgnoreLine,
            output: null);
        try
        {
            _ = await Assert.That(async () => await PosixServerChild.SpawnAsync(start, seams)).Throws<InvalidOperationException>();
            var afterThrow = new ProcessGroupSignal().ProbeProcess(spawn.ProcessId);

            await Assert.That(spawn.ProcessId).IsGreaterThan(1);
            await Assert.That(afterThrow).IsEqualTo(SignalDelivery.NoSuchTarget);
            Console.WriteLine("branch: POSIX — the unadopted child " + spawn.ProcessId.ToString(CultureInfo.InvariantCulture) + " was killed and reaped before the start threw");
        }
        finally
        {
            await EndUnadoptedAsync(spawn.ProcessId, cancellationToken);
        }
    }

    private static ServerChildStart Start(OpenCodeServerOptions options, Action<string> onStandardOutput, OpenCodeServerOutput? output) =>
        new()
        {
            Executable = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess()).Resolve(options.Command[0]),
            SuppliedArguments = [.. options.Command.Skip(1)],
            LauncherArguments = [],
            Environment = options.Environment,
            Password = nameof(PosixServerChildTests),
            GracefulShutdownTimeout = options.GracefulShutdownTimeout,
            Output = output,
            OnStandardOutput = onStandardOutput,
            OnStandardError = output is null ? IgnoreLine : output.AppendStandardError,
        };

    /// <summary>A readiness line that never arrives: the handler that would deliver it throws instead.</summary>
    private static async Task<string> NeverReadyAsync(CancellationToken cancellationToken)
    {
        var never = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => never.TrySetCanceled(cancellationToken)))
        {
            return await never.Task;
        }
    }

    private static async Task<int> GroupChildAsync(OpenCodeServerOutput output, CancellationToken cancellationToken) =>
        (await LiveReadiness.WaitAsync(
            _ => Task.FromResult(LadderTree.Pids(output)),
            static pids => pids.ContainsKey(LadderTree.GroupChild),
            "the ladder tree's group child pid",
            cancellationToken))[LadderTree.GroupChild];

    private static async Task AssertWindowsStartRunsOnProcessAsync(CancellationToken cancellationToken)
    {
        var start = Start(
            new OpenCodeServerOptions { Command = ["bun", "-e", IdleStandIn], GracefulShutdownTimeout = TimeSpan.Zero },
            IgnoreLine,
            output: null);
        var child = await ServerChild.LaunchAsync(start, LauncherSeams.ForCurrentProcess());
        try
        {
            await Assert.That(child).IsTypeOf<ProcessServerChild>();
            await child.EndAsync();
        }
        finally
        {
            await child.DisposeAsync();
            ProcessObservation.KillIfRunning(child.ProcessId);
        }

        await Assert.That(await ProcessObservation.ObserveExitWithinAsync(child.ProcessId, ObservationBound, cancellationToken)).IsTrue();
        Console.WriteLine("branch: Windows — the start ran on Process, and disposal ended pid " + child.ProcessId.ToString(CultureInfo.InvariantCulture));
    }

    /// <summary>Kills and reaps a child of this process that the launcher left behind, when there is one.</summary>
    private static async Task EndUnadoptedAsync(int processId, CancellationToken cancellationToken)
    {
        if (processId <= 1 || new ProcessGroupSignal().ProbeProcess(processId) is SignalDelivery.NoSuchTarget)
        {
            return;
        }

        _ = new ProcessGroupSignal().SignalGroup(processId, ProcessSignal.Kill);
        _ = new ProcessGroupSignal().SignalProcess(processId, ProcessSignal.Kill);
        var exits = new ChildExitStatusReader();
        _ = await LiveReadiness.WaitAsync(
            _ => Task.FromResult(exits.Reap(processId)),
            static status => status is not null,
            "the unadopted child's reap",
            cancellationToken);
    }
}
