using System.Globalization;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Launcher;

/// <summary>
/// The shared readiness, end, and release flow against real processes, on each platform's
/// strategy: a line whose reader delivers it only after disposal ended the server, and the paths
/// <c>OpenCodeServer</c> never takes on purpose: a readiness wait that an unexpected failure leaves
/// before any ladder ran, a line handler that throws, and a spawned child the launcher cannot
/// adopt. The flow is the same on every OS; the platform's ladder decides how the server ends, so
/// each test asserts the end its platform gives: <c>SIGTERM</c> to the group on Linux and macOS,
/// <c>taskkill /T /F</c> and exit code 1 on Windows. Every process a test starts is ended by its pid
/// before the test returns. Keyless <c>[NotInParallel]</c>: the proofs ride wall-clock bounds.
/// </summary>
[NotInParallel]
public sealed class OwnedServerChildTests
{
    private const string FailedReadiness = "the readiness wait failed";

    private const string ReadyLine = "READY";

    private const string BodyLine = "BODY";

    /// <summary>The code upstream's forced end gives a Windows server, <c>taskkill /F</c>'s and <c>TerminateProcess(h, 1)</c>'s alike.</summary>
    private const int ForcedExitCode = 1;

    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);

    private static readonly Action<string> IgnoreLine = static _ => { };

    /// <summary>How long a test waits for a process it observes to end.</summary>
    private static readonly TimeSpan ObservationBound = TimeSpan.FromSeconds(10);

    /// <summary>How a server ended by the ladder of this platform reports its end.</summary>
    private static ChildExitStatus EndedByLadder =>
        OperatingSystem.IsWindows() ? new ChildExitStatus { ExitCode = ForcedExitCode } : new ChildExitStatus { Signal = 15 };

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
        var output = new OpenCodeServerOutput();
        var readyLine = new TaskCompletionSource<string>(TaskCreationOptions.RunContinuationsAsynchronously);
        var bodyHeld = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var deliverBody = new ManualResetEventSlim();
        IReadOnlyList<string> command = OperatingSystem.IsWindows()
            ? ["bun", "-e", "console.log('" + ReadyLine + "'); console.log('" + BodyLine + "'); setInterval(() => {}, 1000);"]
            : ["/bin/sh", "-c", "echo " + ReadyLine + "; echo " + BodyLine + "; exec sleep 60"];
        var start = Start(
            new OpenCodeServerOptions { Command = [.. command], GracefulShutdownTimeout = Grace },
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
        OwnedServerChild? child = null;
        try
        {
            child = await ServerChild.LaunchAsync(start, LauncherSeams.ForCurrentProcess());
            var outcome = await child.WaitForReadinessAsync(readyLine.Task, cancellationToken);
            await bodyHeld.Task.WaitAsync(ObservationBound, cancellationToken);
            var beforeDisposal = output.GetSnapshot().StandardOutput;
            var ending = child.EndAsync();
            var exit = await child.Exited.WaitAsync(ObservationBound, cancellationToken);
            deliverBody.Set();
            await ending.WaitAsync(ObservationBound, cancellationToken);

            await Assert.That(outcome.ReadyLine).IsEqualTo(ReadyLine);
            await Assert.That(beforeDisposal).IsEquivalentTo([ReadyLine], CollectionOrdering.Matching);
            await Assert.That(exit).IsEqualTo(EndedByLadder);
            await Assert.That(output.GetSnapshot().StandardOutput).IsEquivalentTo([ReadyLine, BodyLine], CollectionOrdering.Matching);
            Console.WriteLine("branch: " + Platform() + " — the root " + exit.Describe() + ", and the line its reader delivered afterwards reached the final snapshot");
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
    /// so the root and its child end, and the exit observation and the readers end with them.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task DisposeAsync_Should_End_The_Server_And_Its_Group_When_No_Ladder_Ran(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();
        var start = Start(LadderTree.Options(output, Grace, LadderTree.GroupChild), output.AppendStandardOutput, output);
        OwnedServerChild? child = null;
        try
        {
            child = await ServerChild.LaunchAsync(start, LauncherSeams.ForCurrentProcess());
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
            await Assert.That(exit).IsEqualTo(EndedByLadder);
            await Assert.That(groupChildEnded).IsTrue();
            await Assert.That(child.ReadersEnded.IsCompleted).IsTrue();
            Console.WriteLine("branch: " + Platform() + " — the root " + exit.Describe() + ", and its child " + groupChild.ToString(CultureInfo.InvariantCulture) + " was ended by disposal");
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
    /// code while its child runs on. The ended reader is an ended stream to the readiness wait, not
    /// an exception out of it: the start fails as an exit, and disposal releases everything without
    /// raising the handler's failure. The child ends either way: on Linux and macOS the failed
    /// start's ladder ends the root's group; on Windows the ladder leaves an exited root alone, and
    /// the child, which the server's own runtime placed in its job, ends with the root.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task WaitForReadinessAsync_Should_End_The_Group_When_A_Line_Handler_Throws(CancellationToken cancellationToken)
    {
        var output = new OpenCodeServerOutput();
        var start = Start(
            LadderTree.Options(output, Grace, LadderTree.GroupChild, "exit-at-ready=3"),
            static _ => throw new InvalidOperationException(FailedReadiness),
            output);
        OwnedServerChild? child = null;
        try
        {
            child = await ServerChild.LaunchAsync(start, LauncherSeams.ForCurrentProcess());
            var groupChild = await GroupChildAsync(output, cancellationToken);
            var outcome = await child.WaitForReadinessAsync(NeverReadyAsync(cancellationToken), cancellationToken);
            var groupChildEnded = await ProcessObservation.ObserveExitWithinAsync(groupChild, ObservationBound, cancellationToken);
            await child.DisposeAsync();

            await Assert.That(outcome.ReadyLine).IsNull();
            await Assert.That(outcome.Exit).IsEqualTo(new ChildExitStatus { ExitCode = 3 });
            await Assert.That(groupChildEnded).IsTrue();
            await Assert.That(child.ReadersEnded.IsCompleted).IsTrue();
            Console.WriteLine("branch: " + Platform() + " — the root " + outcome.Exit!.Describe() + ", and its child " + groupChild.ToString(CultureInfo.InvariantCulture) + " ended");
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
    /// A spawned child the launcher cannot adopt is ended by the failing start itself, inside the
    /// bound, with no exit observation to race it. On Linux and macOS it is killed with its group
    /// and reaped, so once the start threw its pid names no process at all, not even a zombie. On
    /// Windows the child was still suspended: it is ended with code 1 before it ran an instruction.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task SpawnAsync_Should_End_A_Child_It_Cannot_Adopt(CancellationToken cancellationToken)
    {
        using var spawn = new SpawnWithoutStandardOutput();
        var seams = LauncherSeams.ForCurrentProcess() with { Spawn = spawn, WindowsSpawn = spawn };
        IReadOnlyList<string> command = OperatingSystem.IsWindows() ? ["bun", "-e", "setInterval(() => {}, 1000);"] : ["/bin/sh", "-c", "exec sleep 60"];
        var start = Start(new OpenCodeServerOptions { Command = [.. command] }, IgnoreLine, output: null);
        try
        {
            _ = await Assert.That(async () => await ServerChild.LaunchAsync(start, seams)).Throws<InvalidOperationException>();

            await Assert.That(spawn.ProcessId).IsGreaterThan(1);
            if (OperatingSystem.IsWindows())
            {
                var held = spawn.Held!;
                await Assert.That(await ProcessObservation.ObserveExitWithinAsync(held, ObservationBound, cancellationToken)).IsTrue();
                await Assert.That(held.ExitCode).IsEqualTo(ForcedExitCode);
                Console.WriteLine("branch: Windows — the unadopted child " + spawn.ProcessId.ToString(CultureInfo.InvariantCulture) + " was ended with code 1 before the start threw");
                return;
            }

            var afterThrow = new ProcessGroupSignal().ProbeProcess(spawn.ProcessId);
            await Assert.That(afterThrow).IsEqualTo(SignalDelivery.NoSuchTarget);
            Console.WriteLine("branch: POSIX — the unadopted child " + spawn.ProcessId.ToString(CultureInfo.InvariantCulture) + " was killed and reaped before the start threw");
        }
        finally
        {
            await EndUnadoptedAsync(spawn, cancellationToken);
        }
    }

    private static string Platform() => OperatingSystem.IsWindows() ? "Windows" : "POSIX";

    private static ServerChildStart Start(OpenCodeServerOptions options, Action<string> onStandardOutput, OpenCodeServerOutput? output) =>
        new()
        {
            Executable = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess()).Resolve(options.Command[0]),
            SuppliedArguments = [.. options.Command.Skip(1)],
            LauncherArguments = [],
            Environment = options.Environment,
            Password = nameof(OwnedServerChildTests),
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

    /// <summary>Ends a child the launcher left behind, when there is one.</summary>
    private static async Task EndUnadoptedAsync(SpawnWithoutStandardOutput spawn, CancellationToken cancellationToken)
    {
        var processId = spawn.ProcessId;
        if (OperatingSystem.IsWindows())
        {
            if (spawn.Held is { } held && !held.HasExited)
            {
                held.Kill();
                _ = await ProcessObservation.ObserveExitWithinAsync(held, ObservationBound, CancellationToken.None);
            }

            return;
        }

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
