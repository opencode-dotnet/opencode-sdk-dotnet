using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Hosting.Launcher;

/// <summary>The launch cancellation boundary and readiness verdicts.</summary>
[NotInParallel]
public sealed class OpenCodeServerReadinessTests
{
    private const string Waiting = "waiting for readiness";
    private const string ReadyEndpoint = "http://127.0.0.1:4096";
    private const string ExitCommand = "process.exit(23);";
    private const string EarlyExitDiagnostic = "exited with code 23";
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);
    private static readonly TimeSpan Grace = TimeSpan.FromSeconds(5);
    private static readonly TimeSpan ReadinessBound = TimeSpan.FromMinutes(2);
    private static readonly TimeSpan CleanupObservationBound = Grace + (OperatingSystem.IsWindows()
        ? WindowsLadderBounds.Default.TreeKill + WindowsLadderBounds.Default.ForcedExit
        : PosixServerLadder.ForcedExitTimeout) + OwnedServerChild.EarlyEndBound + HangGuard;

    [Test]
    public async Task StartAsync_Should_Not_Spawn_When_The_Caller_Is_Canceled_Before_Launch()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelOnWorkerAsync();
        var windowsSpawn = Substitute.For<IWindowsSpawn>();
        var posixSpawn = Substitute.For<IPosixSpawn>();
        var output = new OpenCodeServerOutput();
        var seams = LauncherSeams.ForCurrentProcess() with { WindowsSpawn = windowsSpawn, Spawn = posixSpawn };

        var exception = await Assert.That(async () => await OpenCodeServer.StartWithSeamsAsync(
                new OpenCodeServerOptions { Command = ["bun", "--version"], Output = output }, seams, caller.Token))
            .Throws<Exception>();

        await Assert.That(windowsSpawn.ReceivedCalls()).IsEmpty();
        await Assert.That(posixSpawn.ReceivedCalls()).IsEmpty();
        await Assert.That(exception).IsAssignableTo<OperationCanceledException>();
        await Assert.That(((OperationCanceledException)exception!).CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(output.TryBind()).IsFalse();
        output.AppendStandardOutput("late");
        output.AppendStandardError("late");
        await Assert.That(output.GetSnapshot().StandardOutput).IsEmpty();
        await Assert.That(output.GetSnapshot().StandardError).IsEmpty();
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_End_An_Already_Launched_Child_When_The_Caller_Cancels(CancellationToken cancellationToken)
    {
        using var caller = new CancellationTokenSource();
        var output = new OpenCodeServerOutput();
        var time = new ManualTimeProvider();
        var starting = OpenCodeServer.StartWithSeamsAsync(SilentOptions(output),
            LauncherSeams.ForCurrentProcess() with { Time = time }, caller.Token);
        try
        {
            await time.WaitForArmedAsync(ReadinessBound, HangGuard);
            await WaitForChildAsync(output, cancellationToken);
            await caller.CancelOnWorkerAsync();
            Task[] startingTasks = [starting];
            await AssertStartCanceledAsync(startingTasks, caller.Token, cancellationToken);
            await AssertFinalizedAsync(output);
        }
        finally
        {
            await caller.CancelOnWorkerAsync();
            // Cleanup must join the start even when the test's token has fired.
            Task[] cleanupTasks = [starting];
            _ = await Assert.That(async () => await Task.WhenAll(cleanupTasks).WaitAsync(CancellationToken.None)).Throws<OperationCanceledException>();
            await Assert.That(await LadderTree.EndEveryReportedProcessAsync(output)).IsEmpty();
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task StartAsync_Should_Keep_A_Readiness_Timeout_When_The_Caller_Cancels_During_Teardown(CancellationToken cancellationToken)
    {
        using var caller = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var output = new OpenCodeServerOutput();
        var time = new ManualTimeProvider();
        var seams = GateCleanup(LauncherSeams.ForCurrentProcess() with { Time = time }, entered, release);
        var starting = OpenCodeServer.StartWithSeamsAsync(SilentOptions(output), seams, caller.Token);
        Task? expiry = null;
        try
        {
            await time.WaitForArmedAsync(ReadinessBound, HangGuard);
            await WaitForChildAsync(output, cancellationToken);
            // The expiry must finish even after the caller cancels; the finally joins it.
            expiry = Task.Run(() => time.Advance(ReadinessBound), CancellationToken.None);
            await entered.Task.WaitAsync(HangGuard, cancellationToken);
            await caller.CancelOnWorkerAsync();
            release.Set();
            await expiry.WaitAsync(HangGuard, cancellationToken);

            Task[] startingTasks = [starting];
            var exception = await Assert.That(async () => await Task.WhenAll(startingTasks).WaitAsync(CleanupObservationBound, cancellationToken))
                .Throws<OpenCodeServerException>();
            await Assert.That(exception!.Message).Contains("did not report readiness within 120s");
            await Assert.That(exception.Message).Contains(Waiting);
            await AssertFinalizedAsync(output);
        }
        finally
        {
            release.Set();
            await caller.CancelOnWorkerAsync();
            if (expiry is not null)
            {
                await expiry;
            }

            // Cleanup must join the start even when the test's token has fired.
            Task[] cleanupTasks = [starting];
            _ = await Assert.That(async () => await Task.WhenAll(cleanupTasks).WaitAsync(CancellationToken.None)).Throws<Exception>();
            await Assert.That(await LadderTree.EndEveryReportedProcessAsync(output)).IsEmpty();
        }
    }

    [Test]
    [Timeout(120_000)]
    public Task StartAsync_Should_Prefer_A_Recorded_Ready_Line_At_The_Readiness_Timeout(CancellationToken cancellationToken) =>
        AssertRecordedAtTimeoutAsync("console.log(JSON.stringify({url:'" + ReadyEndpoint + "'}));", null, cancellationToken);

    [Test]
    [Timeout(120_000)]
    public Task StartAsync_Should_Report_A_Recorded_Exit_At_The_Readiness_Timeout(CancellationToken cancellationToken) =>
        AssertRecordedAtTimeoutAsync(ExitCommand, EarlyExitDiagnostic, cancellationToken);

    [Test]
    [Timeout(120_000)]
    public Task StartAsync_Should_Report_Closed_Stdout_At_The_Readiness_Timeout(CancellationToken cancellationToken) =>
        AssertRecordedAtTimeoutAsync(string.Empty, "closed its standard output", cancellationToken);

    [Test]
    [Timeout(120_000)]
    public Task StartAsync_Should_Prefer_Caller_Cancellation_Over_A_Recorded_Ready_Line(CancellationToken cancellationToken) =>
        AssertRecordedAtTimeoutAsync("console.log(JSON.stringify({url:'" + ReadyEndpoint + "'}));", null, cancellationToken,
            static async (time, arbitrate, caller) =>
            {
                await caller.CancelOnWorkerAsync();
                _ = arbitrate.TrySetResult();
            });

    [Test]
    [Timeout(120_000)]
    public Task StartAsync_Should_Keep_A_Recorded_Exit_When_The_Caller_Cancels_During_The_Drain(CancellationToken cancellationToken) =>
        AssertRecordedAtTimeoutAsync(ExitCommand, EarlyExitDiagnostic, cancellationToken, static async (time, arbitrate, caller) =>
        {
            _ = arbitrate.TrySetResult();
            await time.WaitForArmedAsync(OwnedServerChild.EarlyEndBound, Support.HangGuard.Bound, count: 2);
            await caller.CancelOnWorkerAsync();
        });

    private static async Task AssertRecordedAtTimeoutAsync(
        string publish, string? diagnostic, CancellationToken cancellationToken,
        Func<ManualTimeProvider, TaskCompletionSource, CancellationTokenSource, Task>? cancelCaller = null)
    {
        var time = new ManualTimeProvider();
        var entered = new TaskCompletionSource<Task>(TaskCreationOptions.RunContinuationsAsynchronously);
        var canceled = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        var arbitrate = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var caller = new CancellationTokenSource();
        Stream? input = null;
        Stream? originalOutput = null;
        GatedStream? closingOutput = null;
        GatedStream? pendingOutput = null;
        var processId = 0;
        var seams = ObserveEnding(ObserveSpawn(LauncherSeams.ForCurrentProcess(), (pid, stdin) =>
        {
            processId = pid;
            input = stdin;
        }, stdout => RecordedOutput(stdout, diagnostic,
            original => originalOutput = original, closing => closingOutput = closing, pending => pendingOutput = pending))) with
        {
            Time = time,
            ReadinessWait = HoldCanceledWait(entered, canceled, arbitrate, cancellationToken),
        };
        var starting = OpenCodeServer.StartWithSeamsAsync(RecordedOptions(publish), seams, caller.Token);
        try
        {
            var recorded = await entered.Task.WaitAsync(Support.HangGuard.Bound, cancellationToken);
            await time.WaitForArmedAsync(ReadinessBound, Support.HangGuard.Bound);
            time.Advance(ReadinessBound);
            await canceled.Task.WaitAsync(Support.HangGuard.Bound, cancellationToken);
            // The spawn supplied stdin before the owner entered its wait.
            await PublishRecordedOutcomeAsync(recorded, input!, closingOutput, cancellationToken);
            if (closingOutput is not null)
            {
                await Assert.That(ProcessObservation.IsRunning(processId)).IsTrue();
            }

            if (cancelCaller is null)
            {
                _ = arbitrate.TrySetResult();
            }
            else
            {
                // The callback owns the arbitration release; its drain-event wait has HangGuard.Bound.
                await cancelCaller(time, arbitrate, caller);
            }

            if (diagnostic is not null)
            {
                await AdvanceEarlyEndAsync(time, starting, diagnostic == EarlyExitDiagnostic ? 2 : 1, cancellationToken);
            }

            if (cancelCaller is not null && diagnostic is null)
            {
                await AssertCallerCancellationAsync(starting, caller, processId, cancellationToken);
            }
            else
            {
                await AssertStartOutcomeAsync(starting, diagnostic, processId, cancellationToken);
                if (diagnostic is null)
                {
                    await Assert.That(seams.TreeKill.ReceivedCalls()).IsEmpty();
                    await Assert.That(seams.Signals.ReceivedCalls()).IsEmpty();
                }
            }
        }
        finally
        {
            _ = arbitrate.TrySetResult();
            closingOutput?.Release();
            pendingOutput?.Release();
            try
            {
                await caller.CancelOnWorkerAsync();
            }
            finally
            {
                await ReleaseStartAsync(starting, originalOutput, processId);
            }
        }
    }

    private static Stream? RecordedOutput(
        Stream? stdout, string? diagnostic, Action<Stream?> retainOriginal, Action<GatedStream> captureClosing,
        Action<GatedStream> capturePending)
    {
        if (diagnostic is null)
        {
            return stdout;
        }

        retainOriginal(stdout);
        // A BOM followed by EOF carries no line; the gate decides when stdout ends.
        if (diagnostic == "closed its standard output")
        {
            var closing = new GatedStream([0xef, 0xbb, 0xbf], 1);
            captureClosing(closing);
            return closing;
        }

        // Keep stdout pending so only the root exit can complete the owner's wait.
        var pending = new GatedStream([0xef, 0xbb, 0xbf], 1);
        capturePending(pending);
        return pending;
    }

    private static async Task PublishRecordedOutcomeAsync(
        Task recorded, Stream input, GatedStream? closingOutput, CancellationToken cancellationToken)
    {
        await input.WriteAsync(new byte[] { 1 }, cancellationToken).AsTask().WaitAsync(Support.HangGuard.Bound, cancellationToken);
        await input.FlushAsync(cancellationToken).WaitAsync(Support.HangGuard.Bound, cancellationToken);
        closingOutput?.Release();
        Task[] recordedTasks = [recorded];
        await Task.WhenAll(recordedTasks).WaitAsync(Support.HangGuard.Bound, cancellationToken);
    }

    private static async Task AssertCallerCancellationAsync(
        Task<OpenCodeServer> starting, CancellationTokenSource caller, int processId, CancellationToken cancellationToken)
    {
        Task[] startingTasks = [starting];
        await AssertStartCanceledAsync(startingTasks, caller.Token, cancellationToken);
        await Assert.That(ProcessObservation.IsRunning(processId)).IsFalse();
    }

    private static async Task AssertStartCanceledAsync(Task[] startingTasks, CancellationToken callerToken, CancellationToken cancellationToken)
    {
        var exception = await Assert.That(async () => await Task.WhenAll(startingTasks).WaitAsync(CleanupObservationBound, cancellationToken))
            .Throws<OperationCanceledException>();
        await Assert.That(exception!.CancellationToken).IsEqualTo(callerToken);
    }

    private static async Task AdvanceEarlyEndAsync(
        ManualTimeProvider time, Task starting, int count, CancellationToken cancellationToken)
    {
        // A timeout can finish cleanup without a drain; a recorded early end drains first.
        var draining = time.WaitForArmedAsync(OwnedServerChild.EarlyEndBound, Support.HangGuard.Bound, count);
        Task[] startingTasks = [starting];
        if (await Task.WhenAny(Task.WhenAll(startingTasks), draining).WaitAsync(Support.HangGuard.Bound, cancellationToken) == draining)
        {
            await draining.WaitAsync(Support.HangGuard.Bound, cancellationToken);
            time.Advance(OwnedServerChild.EarlyEndBound);
        }
    }

    private static LauncherSeams ObserveSpawn(
        LauncherSeams platform, Action<int, Stream?> capture, Func<Stream?, Stream?> standardOutput)
    {
        var windowsSpawn = Substitute.For<IWindowsSpawn>();
        _ = windowsSpawn.Spawn(Arg.Any<WindowsSpawnRequest>()).Returns(call =>
        {
            var spawned = platform.WindowsSpawn.Spawn(call.Arg<WindowsSpawnRequest>());
            capture(spawned.ProcessId, spawned.StandardInput);
            return spawned with { StandardOutput = standardOutput(spawned.StandardOutput) };
        });
        var posixSpawn = Substitute.For<IPosixSpawn>();
        _ = posixSpawn.Spawn(Arg.Any<PosixSpawnRequest>()).Returns(call =>
        {
            var spawned = platform.Spawn.Spawn(call.Arg<PosixSpawnRequest>());
            capture(spawned.ProcessId, spawned.StandardInput);
            return spawned with { StandardOutput = standardOutput(spawned.StandardOutput) };
        });
        return platform with { WindowsSpawn = windowsSpawn, Spawn = posixSpawn };
    }

    private static LauncherSeams ObserveEnding(LauncherSeams platform)
    {
        var treeKill = Substitute.For<IWindowsTreeKill>();
        _ = treeKill.KillTreeAsync(Arg.Any<int>(), Arg.Any<TimeSpan>()).Returns(call =>
            platform.TreeKill.KillTreeAsync(call.Arg<int>(), call.Arg<TimeSpan>()));
        var signals = Substitute.For<IProcessGroupSignal>();
        _ = signals.SignalGroup(Arg.Any<int>(), Arg.Any<ProcessSignal>()).Returns(call =>
            platform.Signals.SignalGroup(call.Arg<int>(), call.Arg<ProcessSignal>()));
        _ = signals.SignalProcess(Arg.Any<int>(), Arg.Any<ProcessSignal>()).Returns(call =>
            platform.Signals.SignalProcess(call.Arg<int>(), call.Arg<ProcessSignal>()));
        _ = signals.ProbeGroup(Arg.Any<int>()).Returns(call => platform.Signals.ProbeGroup(call.Arg<int>()));
        return platform with { TreeKill = treeKill, Signals = signals };
    }

    private static Func<Task, CancellationToken, Task> HoldCanceledWait(
        TaskCompletionSource<Task> entered, TaskCompletionSource canceled, TaskCompletionSource arbitrate,
        CancellationToken cancellationToken) => async (pending, token) =>
    {
        _ = entered.TrySetResult(pending);
        try
        {
            Task[] pendingTasks = [pending];
            await Task.WhenAll(pendingTasks).WaitAsync(token).WaitAsync(Support.HangGuard.Bound, cancellationToken);
        }
        catch (OperationCanceledException) when (token.IsCancellationRequested)
        {
            _ = canceled.TrySetResult();
            Task[] arbitrationTasks = [arbitrate.Task];
            await Task.WhenAll(arbitrationTasks).WaitAsync(Support.HangGuard.Bound, cancellationToken);
            throw;
        }
    };

    private static async Task AssertStartOutcomeAsync(
        Task<OpenCodeServer> starting, string? diagnostic, int processId, CancellationToken cancellationToken)
    {
        Task<OpenCodeServer>[] startingTasks = [starting];
        if (diagnostic is not null)
        {
            var exception = await Assert.That(async () => await Task.WhenAll(startingTasks).WaitAsync(CleanupObservationBound, cancellationToken))
                .Throws<OpenCodeServerException>();
            await Assert.That(exception!.Message).IsEqualTo("The server " + diagnostic + " before reporting readiness.");
            await Assert.That(ProcessObservation.IsRunning(processId)).IsFalse();
        }
        else
        {
            await Assert.That(async () => await Task.WhenAll(startingTasks).WaitAsync(Support.HangGuard.Bound, cancellationToken)).ThrowsNothing();
            var server = await startingTasks[0];
            await Assert.That(server.Endpoint).IsEqualTo(new Uri(ReadyEndpoint));
            await Assert.That(server.ChildExited.IsCompleted).IsFalse();
        }
    }

    private static async Task ReleaseStartAsync(Task<OpenCodeServer> starting, Stream? originalOutput, int processId)
    {
        try
        {
            // Observe a failed start even when an earlier assertion failed; cleanup outlives caller cancellation.
            await starting.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).WaitAsync(CleanupObservationBound);
        }
        finally
        {
            try
            {
                if (starting.Status == TaskStatus.RanToCompletion)
                {
                    Task<OpenCodeServer>[] startingTasks = [starting];
                    await Task.WhenAll(startingTasks).WaitAsync(CleanupObservationBound);
                    var server = await startingTasks[0];
                    await server.DisposeAsync().AsTask().WaitAsync(CleanupObservationBound);
                }
            }
            finally
            {
                try
                {
                    if (originalOutput is not null)
                    {
                        await originalOutput.DisposeAsync().AsTask().WaitAsync(Support.HangGuard.Bound);
                    }
                }
                finally
                {
                    try
                    {
                        ProcessObservation.KillIfRunning(processId);
                    }
                    finally
                    {
                        // Closing retained pipes and ending the root also settle cleanup after a failed join.
                        await starting.ContinueWith(static completed => _ = completed.Exception, CancellationToken.None,
                            TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default).WaitAsync(CleanupObservationBound);
                    }
                }
            }
        }
    }

    private static OpenCodeServerOptions RecordedOptions(string publish) => new()
    {
        Command = ["bun", "-e", "process.stdin.once('data', () => {" + publish + "}); setInterval(() => {}, 1000);"],
        ReadinessTimeout = ReadinessBound,
        GracefulShutdownTimeout = Grace,
    };

    private static OpenCodeServerOptions SilentOptions(OpenCodeServerOutput output) => new()
    {
        Command = ["bun", "-e", "console.error('ladder-tree role=root pid=' + process.pid); console.error('" + Waiting + "'); setInterval(() => {}, 1000);"],
        ReadinessTimeout = ReadinessBound,
        GracefulShutdownTimeout = Grace,
        Output = output,
    };

    private static async Task WaitForChildAsync(OpenCodeServerOutput output, CancellationToken cancellationToken) =>
        _ = await LiveReadiness.WaitAsync(_ => Task.FromResult(output.GetSnapshot()),
            snapshot => snapshot.StandardError.Contains(Waiting, StringComparer.Ordinal), Waiting, cancellationToken);

    private static async Task AssertFinalizedAsync(OpenCodeServerOutput output)
    {
        await Assert.That(ProcessObservation.IsRunning(LadderTree.Pid(output, LadderTree.Root))).IsFalse();
        var before = output.GetSnapshot().StandardError.Count;
        output.AppendStandardError("late");
        await Assert.That(output.GetSnapshot().StandardError.Count).IsEqualTo(before);
    }

    private static LauncherSeams GateCleanup(LauncherSeams seams, TaskCompletionSource entered, ManualResetEventSlim release)
    {
        var treeKill = Substitute.For<IWindowsTreeKill>();
        _ = treeKill.KillTreeAsync(Arg.Any<int>(), Arg.Any<TimeSpan>()).Returns(call =>
        {
            _ = entered.TrySetResult();
            if (!release.Wait(HangGuard))
            {
                throw new TimeoutException("The test did not release the cleanup gate.");
            }

            return seams.TreeKill.KillTreeAsync(call.Arg<int>(), call.Arg<TimeSpan>());
        });
        var signals = Substitute.For<IProcessGroupSignal>();
        _ = signals.SignalGroup(Arg.Any<int>(), Arg.Any<ProcessSignal>()).Returns(call =>
        {
            _ = entered.TrySetResult();
            if (!release.Wait(HangGuard))
            {
                throw new TimeoutException("The test did not release the cleanup gate.");
            }

            return seams.Signals.SignalGroup(call.Arg<int>(), call.Arg<ProcessSignal>());
        });
        _ = signals.SignalProcess(Arg.Any<int>(), Arg.Any<ProcessSignal>()).Returns(call =>
            seams.Signals.SignalProcess(call.Arg<int>(), call.Arg<ProcessSignal>()));
        _ = signals.ProbeGroup(Arg.Any<int>()).Returns(call => seams.Signals.ProbeGroup(call.Arg<int>()));
        return seams with { TreeKill = treeKill, Signals = signals };
    }
}
