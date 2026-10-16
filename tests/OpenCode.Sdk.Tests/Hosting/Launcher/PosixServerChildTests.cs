using System.Reflection;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Hosting.Launcher;

public sealed class PosixServerChildTests
{
    private const int ProcessId = 12345;

    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(10);

    [Test]
    public async Task SpawnAsync_Should_Declare_A_Reasoned_Best_Effort_Background_Reap_Suppression()
    {
        var reaping = typeof(PosixServerChild).GetMethods(BindingFlags.NonPublic | BindingFlags.Static)
            .Single(method => string.Equals(method.Name, "ReapWithinAsync", StringComparison.Ordinal));
        var suppressions = SuppressionDeclaration.BestEffortFailures(reaping);

        await Assert.That(suppressions).Count().IsEqualTo(1);
        await Assert.That(suppressions.Single().Reason).IsEqualTo(
            "The failed start already threw to its caller, no caller remains to receive a reap fault, and the system reaps an orphaned child when the host exits.");
    }

    [Test]
    public async Task SpawnAsync_Should_Close_The_Pipes_And_Propagate_An_Unexpected_Foreground_Reap_Failure()
    {
        var seams = Seams();
        using var standardInput = new MemoryStream();
        using var standardError = new MemoryStream();
        SpawnWithoutOutput(seams, standardInput, standardError);
        var failure = new IOException("The exit status could not be read.");
        seams.Exits.Reap(ProcessId).Throws(failure);

        var exception = await Assert.That(async () => await PosixServerChild.SpawnAsync(Start(), seams))
            .Throws<IOException>();

        await Assert.That(exception).IsSameReferenceAs(failure);
        await AssertForegroundCleanupAsync(seams, standardInput, standardError);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SpawnAsync_Should_End_Foreground_Reaping_When_The_Child_Is_Reaped_Or_Gone(bool unknownExit)
    {
        var seams = Seams();
        using var standardInput = new MemoryStream();
        using var standardError = new MemoryStream();
        SpawnWithoutOutput(seams, standardInput, standardError);
        seams.Exits.Reap(ProcessId).Returns(unknownExit ? ChildExitStatus.Unknown : new ChildExitStatus { ExitCode = 1 });

        var exception = await Assert.That(async () => await PosixServerChild.SpawnAsync(Start(), seams))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("spawn returned no read end");
        await AssertForegroundCleanupAsync(seams, standardInput, standardError);
    }

    [Test]
    public async Task SpawnAsync_Should_Preserve_The_Adoption_Failure_When_The_Background_Reap_Faults()
    {
        var clock = new ManualTimeProvider();
        var seams = Seams() with { Time = clock };
        using var standardInput = new MemoryStream();
        using var standardError = new MemoryStream();
        SpawnWithoutOutput(seams, standardInput, standardError);
        var foregroundDeadline = clock.GetUtcNow() + PosixServerLadder.ForcedExitTimeout;
        var attemptsAtBound = 0;
        seams.Exits.Reap(ProcessId).Returns(_ => clock.GetUtcNow() >= foregroundDeadline && ++attemptsAtBound == 2
            ? throw new IOException("The background reap failed.")
            : null);

        var spawning = PosixServerChild.SpawnAsync(Start(), seams);
        await clock.WaitForArmedAsync(TimeSpan.FromMilliseconds(10), HangGuard);
        await Assert.That(spawning.IsCompleted).IsFalse();
        clock.Advance(PosixServerLadder.ForcedExitTimeout);
        Task[] spawningTasks = [spawning];
        var exception = await Assert.That(async () => await Task.WhenAll(spawningTasks).WaitAsync(HangGuard))
            .Throws<InvalidOperationException>();

        await Assert.That(exception!.Message).Contains("spawn returned no read end");
        await Assert.That(standardInput.CanRead).IsFalse();
        await Assert.That(standardError.CanRead).IsFalse();
        await Assert.That(attemptsAtBound).IsEqualTo(2);
        await Assert.That(clock.ArmedIn(TimeSpan.FromMilliseconds(100))).IsEqualTo(0);
        _ = seams.Signals.Received(1).SignalGroup(ProcessId, ProcessSignal.Kill);
        _ = seams.Exits.DidNotReceive().WaitUntilExited(Arg.Any<int>());
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SpawnAsync_Should_Map_A_Pipe_Creation_Io_Failure_And_Name_The_Command(bool includeResolutionAndDirectory)
    {
        var start = Start(includeResolutionAndDirectory);
        var seams = Seams();
        var failure = new IOException("The pipe could not be created.");
        seams.Spawn.Spawn(Arg.Any<PosixSpawnRequest>()).Throws(failure);

        var exception = await Assert.That(async () => await PosixServerChild.SpawnAsync(start, seams))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.InnerException).IsSameReferenceAs(failure);
        await Assert.That(exception.Message).IsEqualTo(includeResolutionAndDirectory
            ? "Failed to start the server command 'opencode' (resolved to '/tools/opencode') in the working directory '/workspace'."
            : "Failed to start the server command 'opencode'.");
        _ = seams.Spawn.Received(1).Spawn(Arg.Any<PosixSpawnRequest>());
        _ = seams.Exits.DidNotReceive().Reap(Arg.Any<int>());
        _ = seams.Exits.DidNotReceive().WaitUntilExited(Arg.Any<int>());
        await Assert.That(seams.Signals.ReceivedCalls()).IsEmpty();
    }

    private static async Task AssertForegroundCleanupAsync(LauncherSeams seams, Stream standardInput, Stream standardError)
    {
        await Assert.That(standardInput.CanRead).IsFalse();
        await Assert.That(standardError.CanRead).IsFalse();
        _ = seams.Signals.Received(1).SignalGroup(ProcessId, ProcessSignal.Kill);
        _ = seams.Exits.Received(1).Reap(ProcessId);
        _ = seams.Exits.DidNotReceive().WaitUntilExited(Arg.Any<int>());
    }

    private static ServerChildStart Start(bool includeResolutionAndDirectory = false) => new()
    {
        Executable = new ResolvedExecutable("opencode", includeResolutionAndDirectory ? "/tools/opencode" : "opencode", IsBatchScript: false),
        SuppliedArguments = [],
        LauncherArguments = [],
        Password = "synthetic-test-lease",
        WorkingDirectory = includeResolutionAndDirectory ? "/workspace" : null,
        GracefulShutdownTimeout = TimeSpan.FromSeconds(3),
        OnStandardOutput = static _ => { },
        OnStandardError = static _ => { },
    };

    private static void SpawnWithoutOutput(LauncherSeams seams, Stream standardInput, Stream standardError) =>
        seams.Spawn.Spawn(Arg.Any<PosixSpawnRequest>()).Returns(new PosixSpawnedChild
        {
            ProcessId = ProcessId,
            StandardInput = standardInput,
            StandardError = standardError,
        });

    private static LauncherSeams Seams() => new()
    {
        WindowsSpawn = Substitute.For<IWindowsSpawn>(),
        Job = Substitute.For<IProcessJob>(),
        WindowsExits = Substitute.For<IWindowsChildExit>(),
        TreeKill = Substitute.For<IWindowsTreeKill>(),
        Spawn = Substitute.For<IPosixSpawn>(),
        Signals = Substitute.For<IProcessGroupSignal>(),
        Exits = Substitute.For<IChildExitStatus>(),
        Time = TimeProvider.System,
        HostEnvironment = new Dictionary<string, string>(StringComparer.Ordinal),
    };
}
