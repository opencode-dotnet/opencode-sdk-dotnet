using System.ComponentModel;
using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using NSubstitute;
using NSubstitute.ExceptionExtensions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Hosting.Launcher;

/// <summary>The Windows strategy's spawn and adoption failures, including the running child it must end.</summary>
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class WindowsServerChildTests
{
    private static readonly RealFileSystem FileSystem = new();
    private static readonly TimeSpan Bound = TimeSpan.FromSeconds(30);

    /// <summary>A native spawn failure preserves its cause and names the resolution and directory outside the command quotes.</summary>
    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task SpawnAsync_Should_Name_The_Command_And_Preserve_The_Spawn_Failure(bool details)
    {
        var seams = Seams();
        var failure = new Win32Exception(2);
        seams.WindowsSpawn.Spawn(Arg.Any<WindowsSpawnRequest>()).Throws(failure);
        var start = Start(new ResolvedExecutable("opencode", details ? @"C:\tools\opencode.exe" : "opencode", IsBatchScript: false)) with
        { WorkingDirectory = details ? @"C:\work" : null };

        var exception = await Assert.That(async () => await WindowsServerChild.SpawnAsync(start, seams))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.Message).IsEqualTo(details
            ? @"Failed to start the server command 'opencode' (resolved to 'C:\tools\opencode.exe') in the working directory 'C:\work'."
            : "Failed to start the server command 'opencode'.");
        await Assert.That(exception.InnerException).IsSameReferenceAs(failure);
    }

    /// <summary>A watch creation failure ends the already-running child with code 1 and closes the handle it offered the watch; the session leak guard proves no watch is left.</summary>
    [Test]
    [Timeout(120_000)]
    public async Task SpawnAsync_Should_End_The_Child_When_Its_Exit_Cannot_Be_Watched(CancellationToken cancellationToken)
    {
        if (!OperatingSystem.IsWindows())
        {
            BranchReport.Print("POSIX — the Windows watch adoption path requires a Windows process handle");
            return;
        }

        await using var exits = new RefusingChildExit();
        var seams = Seams(exits, exits);
        var fixture = new ServiceFixtureCommand(FileSystem).Resolve();
        var resolved = new ExecutableResolver(ExecutableSearchEnvironment.ForCurrentProcess()).Resolve(fixture[0]);
        var executable = new ResolvedExecutable("fixture", resolved.Path, IsBatchScript: false);
        var start = Start(executable) with { SuppliedArguments = [fixture[1], "idle"] };

        var exception = await Assert.That(async () => await WindowsServerChild.SpawnAsync(start, seams))
            .Throws<OpenCodeServerException>();

        await Assert.That(exception!.Message).IsEqualTo(
            $"The server command 'fixture' (resolved to '{resolved.Path}') was started, but its exit could not be watched or its output read, so it was ended.");
        await Assert.That(exception.InnerException).IsSameReferenceAs(exits.Failure);
        await Assert.That(exits.Held).IsNotNull();
        await Assert.That(await ProcessObservation.ObserveExitWithinAsync(exits.Held!, Bound, cancellationToken)).IsTrue();
        await Assert.That(exits.Held!.ExitCode).IsEqualTo(1);
        await Assert.That(exits.Watched!.IsClosed).IsTrue();
    }

    private static ServerChildStart Start(ResolvedExecutable executable) => new()
    {
        Executable = executable,
        SuppliedArguments = [],
        LauncherArguments = [],
        Password = nameof(WindowsServerChildTests),
        GracefulShutdownTimeout = TimeSpan.FromSeconds(3),
        OnStandardOutput = static _ => { },
        OnStandardError = static _ => { },
    };

    private static LauncherSeams Seams(IWindowsSpawn? spawn = null, IWindowsChildExit? exits = null) => new()
    {
        WindowsSpawn = spawn ?? Substitute.For<IWindowsSpawn>(),
        WindowsExits = exits ?? Substitute.For<IWindowsChildExit>(),
        Job = Substitute.For<IProcessJob>(),
        Signals = Substitute.For<IProcessGroupSignal>(),
        TreeKill = Substitute.For<IWindowsTreeKill>(),
        Exits = Substitute.For<IChildExitStatus>(),
        Time = new ManualTimeProvider(),
        Spawn = Substitute.For<IPosixSpawn>(),
        HostEnvironment = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase),
    };

    /// <summary>Takes an independent observation handle before refusing to create any exit watch.</summary>
    private sealed class RefusingChildExit : IWindowsChildExit, IWindowsSpawn, IAsyncDisposable
    {
        private readonly WindowsSpawn _spawn = new();

        public Win32Exception Failure { get; } = new(6);

        public Process? Held { get; private set; }

        public SafeProcessHandle? Watched { get; private set; }

        public WindowsSpawnedChild Spawn(WindowsSpawnRequest request)
        {
            var child = _spawn.Spawn(request);
            try
            {
                Held = Process.GetProcessById(child.ProcessId);
                _ = Held.Handle;
                return child;
            }
            catch
            {
                // The strategy never saw this child, so nothing else would end it or close its handles.
                _ = WindowsInterop.TerminateProcess(child.Process, 1);
                child.MainThread?.Dispose();
                child.StandardInput?.Dispose();
                child.StandardOutput?.Dispose();
                child.StandardError?.Dispose();
                child.Process.Dispose();
                throw;
            }
        }

        public IWindowsExitWatch Watch(SafeProcessHandle process)
        {
            Watched = process;
            throw Failure;
        }

        public async ValueTask DisposeAsync()
        {
            if (Held is { HasExited: false })
            {
                Held.Kill();
                await Held.WaitForExitAsync();
            }

            Held?.Dispose();
        }
    }
}
