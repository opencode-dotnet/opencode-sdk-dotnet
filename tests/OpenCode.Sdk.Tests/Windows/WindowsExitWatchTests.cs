using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Windows;

/// <summary>
/// The Windows exit watch against real processes: the code the operating system gives, in its full
/// 32 bits, and a release that happens exactly once, after both the exit and the watch's disposal,
/// whichever comes first, so the process handle pins the pid for as long as the child runs and is
/// closed once nobody needs it.
/// </summary>
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class WindowsExitWatchTests
{
    /// <summary><c>STATUS_ACCESS_VIOLATION</c>, the code of a crashed native process, as a signed 32-bit value.</summary>
    private const int AccessViolation = -1073741819;

    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);
    private static readonly RealFileSystem FileSystem = new();

    [Test]
    [Timeout(120_000)]
    [Arguments(AccessViolation)]
    [Arguments(7)]
    public async Task Exited_Should_Report_The_Code_In_Its_Full_32_Bits(int code, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the exit watch decodes a waitpid status there");
            return;
        }

        var child = Spawn("exit-code", code.ToString(CultureInfo.InvariantCulture));
        using var watch = WindowsExitWatch.Start(child.Process);

        var exit = await watch.Exited.WaitAsync(Bound, cancellationToken);

        await Assert.That(exit.ExitCode).IsEqualTo(code);
        BranchReport.Print("Windows — the child " + exit.Describe());
    }

    /// <summary>
    /// Disposed while the child runs, the watch keeps its handles: the process handle still pins the
    /// pid, and the wait still completes. The child's exit then releases them.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Dispose_Should_Keep_The_Handle_Until_A_Running_Child_Exits(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the exit watch is a thread that ends with its child there");
            return;
        }

        var child = Spawn("idle");

        // A handle of the test's own ends the child on every path, whatever the watch did with its.
        using var held = Process.GetProcessById(child.ProcessId);
        _ = held.Handle;
        try
        {
            var process = child.Process;
            var watch = WindowsExitWatch.Start(process);
            watch.Dispose();
            var heldWhileRunning = !process.IsClosed;

            held.Kill();
            var exit = await watch.Exited.WaitAsync(Bound, cancellationToken);
            var released = await ReleasedWithinAsync(process, cancellationToken);

            await Assert.That(heldWhileRunning).IsTrue();
            await Assert.That(exit.ExitCode).IsEqualTo(-1);
            await Assert.That(released).IsTrue();
        }
        finally
        {
            if (!held.HasExited)
            {
                held.Kill();
            }
        }
    }

    /// <summary>The child exits first: the watch keeps its handles until it is disposed, then releases them at once.</summary>
    [Test]
    [Timeout(120_000)]
    public async Task Dispose_Should_Release_At_Once_When_The_Child_Already_Exited(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the exit watch is a thread that ends with its child there");
            return;
        }

        var child = Spawn("exit-code", "0");
        var process = child.Process;
        var watch = WindowsExitWatch.Start(process);
        _ = await watch.Exited.WaitAsync(Bound, cancellationToken);
        var heldAfterExit = !process.IsClosed;

        watch.Dispose();

        await Assert.That(heldAfterExit).IsTrue();
        await Assert.That(process.IsClosed).IsTrue();
    }

    /// <summary>
    /// Disposal timed against the exit, round after round: whichever party comes second releases,
    /// exactly once, and every round's handle ends up closed.
    /// </summary>
    [Test]
    [Timeout(240_000)]
    public async Task Dispose_Should_Release_Exactly_Once_When_It_Races_The_Exit(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the exit watch is a thread that ends with its child there");
            return;
        }

        const int rounds = 10;
        var released = 0;
        for (var round = 0; round < rounds; round++)
        {
            var child = Spawn("exit-code", "0");
            var watch = WindowsExitWatch.Start(child.Process);
            _ = WindowsInterop.TerminateProcess(child.Process, 0);
            watch.Dispose();
            _ = await watch.Exited.WaitAsync(Bound, cancellationToken);
            released += await ReleasedWithinAsync(child.Process, cancellationToken) ? 1 : 0;
        }

        await Assert.That(released).IsEqualTo(rounds);
    }

    private static WindowsSpawnedChild Spawn(params string[] mode)
    {
        var fixture = new ServiceFixtureCommand(FileSystem).Resolve();
        var command = WindowsCommandLine.For(new ResolvedExecutable(fixture[0], fixture[0], IsBatchScript: false), [fixture[1], .. mode], []);
        return new WindowsSpawn().Spawn(new WindowsSpawnRequest { ApplicationPath = command.ApplicationPath, CommandLine = command.CommandLine });
    }

    /// <summary>The release runs on the exit callback's thread, a moment after the exit completes the task.</summary>
    [SlopwatchSuppress(
        "SW004",
        "The release has no signal of its own to await: the exit callback closes the handle a moment after it completes the task the test awaited, so the poll waits for the handle's closed state, bounded by Bound and cancelled with the test.")]
    private static async Task<bool> ReleasedWithinAsync(SafeProcessHandle process, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (!process.IsClosed && waited.Elapsed < Bound)
        {
            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }

        return process.IsClosed;
    }
}
