using System.Diagnostics;
using System.Globalization;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Hosting.Platform.Windows;

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
            await WaitForReleaseAsync(() => process.IsClosed, "the watched process handle to close", cancellationToken);
            var released = process.IsClosed;

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
            await WaitForReleaseAsync(() => child.Process.IsClosed, "the watched process handle to close", cancellationToken);
            released += child.Process.IsClosed ? 1 : 0;
        }

        await Assert.That(released).IsEqualTo(rounds);
    }

    /// <summary>
    /// The wait is registered to run its callback on the operating system's wait thread, once, and
    /// the exit completes there, never in the .NET thread pool's queue, so a busy pool cannot hold
    /// back an exit the kernel has already reported. The flags are read where the watch hands them
    /// to the kernel: a native worker thread is not a pool thread either, so the thread alone could
    /// not tell the wait thread from a native worker.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Exited_Should_Complete_On_The_Native_Wait_Thread_Rather_Than_A_Pool_Thread(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — a dedicated watch thread completes the exit there");
            return;
        }

        var kernel = new RecordingKernel();
        var child = Spawn("exit-code", "0");
        using var watch = WindowsExitWatch.Start(child.Process, kernel);

        _ = await watch.Exited.WaitAsync(Bound, cancellationToken);

        // WT_EXECUTEINWAITTHREAD (0x4) | WT_EXECUTEONLYONCE (0x8), as winnt.h defines them.
        await Assert.That(kernel.RegisteredFlags).IsEqualTo(0x0000000Cu);
        await Assert.That(watch.CompletedOnPoolThread).IsNotNull();
        await Assert.That(watch.CompletedOnPoolThread!.Value).IsFalse();
    }

    /// <summary>
    /// Whatever the order of the exit and the disposals, the registration is unregistered exactly
    /// once, while the handles are still open, and only then are they closed: a second disposal
    /// after the release does nothing.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    [Arguments(ReleaseOrder.DisposeThenExit)]
    [Arguments(ReleaseOrder.ExitThenDispose)]
    [Arguments(ReleaseOrder.DisposeExitDispose)]
    public async Task Release_Should_Unregister_Exactly_Once_Before_Closing_The_Handles(ReleaseOrder order, CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the exit watch is a thread that ends with its child there");
            return;
        }

        string[] mode = order == ReleaseOrder.ExitThenDispose ? ["exit-code", "0"] : ["idle"];
        var child = Spawn(mode);
        var kernel = new RecordingKernel { Process = child.Process };
        var watch = WindowsExitWatch.Start(child.Process, kernel);
        if (order == ReleaseOrder.ExitThenDispose)
        {
            _ = await watch.Exited.WaitAsync(Bound, cancellationToken);
            watch.Dispose();
        }
        else
        {
            watch.Dispose();
            _ = WindowsInterop.TerminateProcess(child.Process, 0);
            _ = await watch.Exited.WaitAsync(Bound, cancellationToken);
            await WaitForReleaseAsync(() => watch.IsReleased, "the Windows exit watch to release", cancellationToken);
            await Assert.That(watch.IsReleased).IsTrue();
            if (order == ReleaseOrder.DisposeExitDispose)
            {
                watch.Dispose();
            }
        }

        await WaitForReleaseAsync(() => watch.IsReleased, "the Windows exit watch to release", cancellationToken);
        await Assert.That(watch.IsReleased).IsTrue();
        await Assert.That(kernel.Unregistrations).IsEqualTo(1);
        await Assert.That(kernel.HandlesOpenAtEveryUnregistration).IsTrue();
        await Assert.That(child.Process.IsClosed).IsTrue();
    }

    /// <summary>
    /// A registration the watch cannot hand over, because something fails after the kernel
    /// registered it, is unregistered before the failure surfaces, so no callback can reach a
    /// context the failed start freed; the process handle stays the caller's.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Start_Should_Unregister_A_Registration_It_Cannot_Hand_Over(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the exit watch is a thread that ends with its child there");
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var child = Spawn("idle");
        var kernel = new RecordingKernel { Process = child.Process, ThrowAfterRegister = true };
        try
        {
            _ = await Assert.That(() => WindowsExitWatch.Start(child.Process, kernel)).Throws<InvalidOperationException>();

            await Assert.That(kernel.Unregistrations).IsEqualTo(1);
            await Assert.That(child.Process.IsClosed).IsFalse();
        }
        finally
        {
            _ = WindowsInterop.TerminateProcess(child.Process, 0);
            child.Process.Dispose();
        }
    }

    /// <summary>
    /// A wait the kernel will not unregister may still call back, so the release keeps the handles
    /// and the callback's context rather than free what a callback could reach, and says so.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Release_Should_Keep_The_Handles_When_The_Wait_Cannot_Be_Unregistered(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the exit watch is a thread that ends with its child there");
            return;
        }

        var child = Spawn("exit-code", "0");
        var kernel = new RecordingKernel { Process = child.Process, FailUnregister = true };
        var watch = WindowsExitWatch.Start(child.Process, kernel);
        _ = await watch.Exited.WaitAsync(Bound, cancellationToken);

        watch.Dispose();

        await Assert.That(kernel.Unregistrations).IsEqualTo(1);
        await Assert.That(watch.ReleaseFailed).IsTrue();
        await Assert.That(watch.IsReleased).IsFalse();
        await Assert.That(child.Process.IsClosed).IsFalse();
    }

    /// <summary>
    /// An exit code that cannot be read reports the exit as unknown, and the exit still arrives: the
    /// disposal that follows releases the registration and the handles, once.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task Exited_Should_Report_Unknown_And_Still_Release_When_The_Code_Cannot_Be_Read(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the exit watch decodes a waitpid status there");
            return;
        }

        var child = Spawn("exit-code", "0");
        var kernel = new RecordingKernel { Process = child.Process, FailRead = true };
        var watch = WindowsExitWatch.Start(child.Process, kernel);

        var exit = await watch.Exited.WaitAsync(Bound, cancellationToken);
        watch.Dispose();

        await Assert.That(exit).IsEqualTo(ChildExitStatus.Unknown);
        await WaitForReleaseAsync(() => watch.IsReleased, "the Windows exit watch to release", cancellationToken);
        await Assert.That(watch.IsReleased).IsTrue();
        await Assert.That(kernel.Unregistrations).IsEqualTo(1);
        await Assert.That(child.Process.IsClosed).IsTrue();
    }

    /// <summary>
    /// A start that fails after the exit watch is registered ends the server and disposes the
    /// watch, which releases its registration and handles once the ended server is gone.
    /// </summary>
    [Test]
    [Timeout(120_000)]
    public async Task A_Start_That_Fails_After_The_Watch_Is_Registered_Should_Release_It(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the exit watch is a thread that ends with its child there");
            return;
        }

        var exits = new RecordingChildExit();

        // A ladder without a tree kill refuses to be built, the first step after the watch.
        var seams = LauncherSeams.ForCurrentProcess() with { WindowsExits = exits, TreeKill = null! };
        var fixture = new ServiceFixtureCommand(FileSystem).Resolve();
        var start = new ServerChildStart
        {
            Executable = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess()).Resolve(fixture[0]),
            SuppliedArguments = [fixture[1], "idle"],
            LauncherArguments = [],
            Password = nameof(WindowsExitWatchTests),
            GracefulShutdownTimeout = TimeSpan.FromSeconds(5),
            OnStandardOutput = static _ => { },
            OnStandardError = static _ => { },
        };

        _ = await Assert.That(async () => await ServerChild.LaunchAsync(start, seams)).Throws<ArgumentNullException>();
        var watch = exits.Started.Single();
        var exit = await watch.Exited.WaitAsync(Bound, cancellationToken);

        await Assert.That(exit.ExitCode).IsEqualTo(1);
        await WaitForReleaseAsync(() => watch.IsReleased, "the Windows exit watch to release", cancellationToken);
        await Assert.That(watch.IsReleased).IsTrue();
        await Assert.That(exits.Kernel.Unregistrations).IsEqualTo(1);
    }

    private static WindowsSpawnedChild Spawn(params string[] mode)
    {
        var fixture = new ServiceFixtureCommand(FileSystem).Resolve();
        var command = WindowsCommandLine.For(new ResolvedExecutable(fixture[0], fixture[0], IsBatchScript: false), [fixture[1], .. mode], []);
        return new WindowsSpawn().Spawn(new WindowsSpawnRequest { ApplicationPath = command.ApplicationPath, CommandLine = command.CommandLine });
    }

    /// <summary>The release closes the handles on the pool, a moment after the exit completes the task.</summary>
    [SlopwatchSuppress(
        "SW004",
        "The release has no signal of its own to await: it closes the handles on the pool after the exit completes the task the test awaited, so the poll waits for the watch's released state or the handle's closed state, bounded by Bound and cancelled with the test.")]
    private static async Task WaitForReleaseAsync(Func<bool> released, string description, CancellationToken cancellationToken)
    {
        var waited = Stopwatch.StartNew();
        while (!released())
        {
            if (waited.Elapsed >= Bound)
            {
                throw new TimeoutException("Timed out waiting for " + description + ".");
            }

            await Task.Delay(TimeSpan.FromMilliseconds(10), cancellationToken);
        }
    }

    /// <summary>The orders in which a watch's exit and its disposals can arrive.</summary>
    public enum ReleaseOrder
    {
        /// <summary>Disposed while the child runs; the exit releases.</summary>
        DisposeThenExit,

        /// <summary>The child exits first; the disposal releases.</summary>
        ExitThenDispose,

        /// <summary>Disposed, then the exit releases, then disposed again, which must do nothing.</summary>
        DisposeExitDispose,
    }

    /// <summary>
    /// The shipped kernel calls, recorded: the flags a wait is registered with, how often it is
    /// unregistered and whether the watched process's handle was still open each time, and, on
    /// request, a read of the exit code that fails.
    /// </summary>
    private sealed class RecordingKernel : IExitWaitKernel
    {
        private int _unregistrations;
        private int _closedAtUnregistration;

        public SafeProcessHandle? Process { get; init; }

        public bool FailRead { get; init; }

        /// <summary>Gets whether the unregistration reports failure, after unregistering for real so no callback outlives the test.</summary>
        public bool FailUnregister { get; init; }

        /// <summary>Gets whether the registration throws after registering for real, as a failure after the kernel call would.</summary>
        public bool ThrowAfterRegister { get; init; }

        public uint? RegisteredFlags { get; private set; }

        public int Unregistrations => Volatile.Read(ref _unregistrations);

        public bool HandlesOpenAtEveryUnregistration => Volatile.Read(ref _closedAtUnregistration) == 0;

        public bool Register(out IntPtr registration, SafeWaitHandle waited, IntPtr callback, IntPtr context, uint flags)
        {
            RegisteredFlags = flags;
            var registered = ExitWaitKernel.Instance.Register(out registration, waited, callback, context, flags);
            return registered && ThrowAfterRegister
                ? throw new InvalidOperationException("The test's injected failure after the registration.")
                : registered;
        }

        public bool Unregister(IntPtr registration)
        {
            _ = Interlocked.Increment(ref _unregistrations);
            if (Process is { IsClosed: true })
            {
                _ = Interlocked.Increment(ref _closedAtUnregistration);
            }

            return ExitWaitKernel.Instance.Unregister(registration) && !FailUnregister;
        }

        public bool TryReadExitCode(SafeProcessHandle process, out uint exitCode) =>
            FailRead
                ? throw new InvalidOperationException("The test's injected read failure.")
                : ExitWaitKernel.Instance.TryReadExitCode(process, out exitCode);
    }

    /// <summary>The shipped exit watch, recorded so a test can follow a watch the launcher started.</summary>
    private sealed class RecordingChildExit : IWindowsChildExit
    {
        public List<WindowsExitWatch> Started { get; } = [];

        public RecordingKernel Kernel { get; } = new();

        public IWindowsExitWatch Watch(SafeProcessHandle process)
        {
            var watch = WindowsExitWatch.Start(process, Kernel);
            Started.Add(watch);
            return watch;
        }
    }

}
