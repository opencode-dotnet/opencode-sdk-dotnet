using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Hosting.Launcher;

/// <summary>The launch cancellation boundary and readiness verdicts.</summary>
[NotInParallel]
public sealed class OpenCodeServerReadinessTests
{
    private const string Waiting = "waiting for readiness";
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
        await caller.CancelAsync();
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
            await caller.CancelAsync();
            var exception = await Assert.That(async () => await starting.WaitAsync(CleanupObservationBound, cancellationToken))
                .Throws<OperationCanceledException>();

            await Assert.That(exception!.CancellationToken).IsEqualTo(caller.Token);
            await AssertFinalizedAsync(output);
        }
        finally
        {
            await caller.CancelAsync();
            // Cleanup must join the start even when the test's token has fired.
            _ = await Assert.That(async () => await starting.WaitAsync(CancellationToken.None)).Throws<OperationCanceledException>();
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
            await caller.CancelAsync();
            release.Set();
            await expiry.WaitAsync(HangGuard, cancellationToken);

            var exception = await Assert.That(async () => await starting.WaitAsync(CleanupObservationBound, cancellationToken))
                .Throws<OpenCodeServerException>();
            await Assert.That(exception!.Message).Contains("did not report readiness within 120s");
            await Assert.That(exception.Message).Contains(Waiting);
            await AssertFinalizedAsync(output);
        }
        finally
        {
            release.Set();
            await caller.CancelAsync();
            if (expiry is not null)
            {
                await expiry;
            }

            // Cleanup must join the start even when the test's token has fired.
            _ = await Assert.That(async () => await starting.WaitAsync(CancellationToken.None)).Throws<Exception>();
            await Assert.That(await LadderTree.EndEveryReportedProcessAsync(output)).IsEmpty();
        }
    }

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
