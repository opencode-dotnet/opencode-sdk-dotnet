using System.Diagnostics;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Internal.Windows.Abstractions;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Windows;

/// <summary>
/// The Windows tree kill against real process trees: one run of the system <c>taskkill /T /F</c>,
/// through the SDK's own spawn with no handle at all, awaited without a thread. Its exit code is the
/// result: zero when it ended every process of the tree, non-zero otherwise (128 when the root is
/// already gone, whose descendants it then cannot reach either). A run that outlives its bound is
/// ended, so nothing it starts outlives the release that started it.
/// </summary>
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class TaskkillTreeKillTests
{
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(10);
    private static readonly RealFileSystem FileSystem = new();

    /// <summary>
    /// The tree is the fixture's tree root and its idle child. Neither member ends because the other
    /// ended, so the run reports success only when it ended both itself.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task KillTreeAsync_Should_Report_Ended_When_Taskkill_Ends_The_Tree(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the launcher ends its server through its process group, never a tree kill");
            return;
        }

        await using var root = await ServiceFixtureProcess.StartTreeRootAsync(FileSystem, cancellationToken);

        // The root holds a handle on its child, so the pid it named is still that child; the handle
        // taken here keeps the pid from being reused until the test is done with it.
        using var child = Process.GetProcessById(root.ChildProcessId);
        _ = child.Handle;
        try
        {
            var outcome = await Kill().KillTreeAsync(root.ProcessId, Bound);

            await Assert.That(outcome).IsEqualTo(TreeKillOutcome.Ended);
            await Assert.That(await root.ObserveExitWithinAsync(Bound, cancellationToken)).IsTrue();
            await Assert.That(await ProcessObservation.ObserveExitWithinAsync(child, Bound, cancellationToken)).IsTrue();
            await Assert.That(child.ExitCode).IsEqualTo(1);
            BranchReport.Print("Windows — taskkill ended the root and its child, each with code 1");
        }
        finally
        {
            if (!child.HasExited)
            {
                child.Kill();
            }
        }
    }

    [Test]
    [Timeout(120_000)]
    public async Task KillTreeAsync_Should_Report_Failed_When_The_Root_Already_Exited(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the launcher ends its server through its process group, never a tree kill");
            return;
        }

        // The held process object keeps the exited root's pid from being reused meanwhile.
        using var root = Process.Start(new ProcessStartInfo
        {
            FileName = BatchCommandLine.InterpreterPath,
            Arguments = "/d /c exit 0",
            UseShellExecute = false,
            CreateNoWindow = true,
        })!;
        await Assert.That(await ProcessObservation.ObserveExitWithinAsync(root, Bound, cancellationToken)).IsTrue();

        var outcome = await Kill().KillTreeAsync(root.Id, Bound);

        await Assert.That(outcome).IsEqualTo(TreeKillOutcome.Failed);
    }

    /// <summary>
    /// The run goes from the absolute system path, with no handle the host holds: the spawn is asked
    /// for no standard stream at all.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task KillTreeAsync_Should_Run_The_System_Taskkill_With_No_Handle_Of_The_Host(CancellationToken cancellationToken)
    {
        var recording = new RecordingSpawn();
        var outcome = await new TaskkillTreeKill(recording, new WindowsChildExit()).KillTreeAsync(4242, Bound).WaitAsync(cancellationToken);
        var request = recording.Requests.Single();

        await Assert.That(outcome).IsEqualTo(TreeKillOutcome.Failed);
        await Assert.That(request.ApplicationPath).IsEqualTo(
            FileSystem.Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe"));
        await Assert.That(request.CommandLine).EndsWith(" /pid 4242 /T /F");
        await Assert.That(request.StandardStreams).IsNull();
        await Assert.That(request.Environment).IsNull();
    }

    /// <summary>
    /// A run that is still going at its bound is ended and reported, without waiting for it any
    /// longer: here the spawn starts a process that never exits in place of taskkill.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task KillTreeAsync_Should_End_A_Run_That_Outlives_Its_Bound(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the launcher ends its server through its process group, never a tree kill");
            return;
        }

        using var hanging = new LingeringSpawn();
        var bound = TimeSpan.FromMilliseconds(500);
        var run = Stopwatch.StartNew();

        var outcome = await new TaskkillTreeKill(hanging, new WindowsChildExit()).KillTreeAsync(4242, bound);
        var elapsed = run.Elapsed;

        await Assert.That(outcome).IsEqualTo(TreeKillOutcome.TimedOut);
        await Assert.That(elapsed).IsLessThan(bound + TimeSpan.FromSeconds(5));
        await Assert.That(await ProcessObservation.ObserveExitWithinAsync(hanging.Held!, Bound, cancellationToken)).IsTrue();
        await Assert.That(hanging.Held!.ExitCode).IsEqualTo(1);
    }

    private static TaskkillTreeKill Kill() => new(new WindowsSpawn(), new WindowsChildExit());

    /// <summary>A spawn that records each request and starts nothing.</summary>
    private sealed class RecordingSpawn : IWindowsSpawn
    {
        public List<WindowsSpawnRequest> Requests { get; } = [];

        public WindowsSpawnedChild Spawn(WindowsSpawnRequest request)
        {
            Requests.Add(request);
            throw new System.ComponentModel.Win32Exception(2);
        }
    }

    /// <summary>A spawn that starts the service fixture's lingering mode instead of what it was asked for, and holds a handle of its own on it.</summary>
    private sealed class LingeringSpawn : IWindowsSpawn, IDisposable
    {
        private readonly WindowsSpawn _spawn = new();

        public Process? Held { get; private set; }

        public WindowsSpawnedChild Spawn(WindowsSpawnRequest request)
        {
            var fixture = new ServiceFixtureCommand(FileSystem).Resolve();
            var command = WindowsCommandLine.For(new ResolvedExecutable(fixture[0], fixture[0], IsBatchScript: false), [fixture[1], "idle"], []);
            var child = _spawn.Spawn(request with { ApplicationPath = command.ApplicationPath, CommandLine = command.CommandLine });
            Held = Process.GetProcessById(child.ProcessId);
            _ = Held.Handle;
            return child;
        }

        public void Dispose()
        {
            if (Held is { HasExited: false })
            {
                Held.Kill();
            }

            Held?.Dispose();
        }
    }
}
