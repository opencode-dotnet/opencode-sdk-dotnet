using System.ComponentModel;
using System.Runtime.ExceptionServices;
using System.Runtime.InteropServices;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// The Windows strategy. The server is created suspended through the SDK's own spawn, with an
/// explicit inherited-handle list, so it receives its three standard handles and nothing else the
/// host holds. While it is suspended it is assigned to the process-wide kill-on-close job, so it
/// runs no instruction outside the job: the job ends it when its owner ends, however the owner
/// ends, and a job a libuv server creates for its own children nests under ours rather than the
/// other way round. Then its main thread is resumed. Its stdin is the ownership lease, a pipe the
/// launcher holds and never writes; its stdout and stderr are overlapped pipes read without
/// holding a thread. Its exit is observed through a registered wait, and it is ended by
/// <see cref="WindowsServerLadder"/>; the readiness, end, and release flow is the shared
/// <see cref="OwnedServerChild"/>.
/// </summary>
/// <remarks>
/// A batch shim is started through cmd.exe, so the job holds cmd.exe and the shim's own child,
/// the real server, is not in it; when the owner dies, that server ends through the stdin lease
/// alone. With <c>OPENCODE_PRINT_LOGS=1</c> in the host's environment, as upstream reads it, the
/// server writes its stderr to the host's own stderr and shares the host's console, as upstream's
/// server does: no stderr tail is quoted, a collector receives no stderr lines, and a Ctrl+C in that
/// console reaches the server too.
/// </remarks>
internal static class WindowsServerChild
{
    /// <summary>Upstream's switch for a server that logs to its owner's stderr.</summary>
    private const string PrintLogsVariable = "OPENCODE_PRINT_LOGS";

    /// <summary>What <c>ResumeThread</c> returns when it fails.</summary>
    private const uint ResumeFailed = 0xFFFFFFFF;

    /// <summary>The exit code a server that could not be adopted is ended with.</summary>
    private const uint AbandonedExitCode = 1;

    /// <summary>Spawns the server, places it in the job, resumes it, and starts reading and watching it.</summary>
    /// <param name="start">What to start.</param>
    /// <param name="seams">The Windows seams and the host's environment.</param>
    /// <returns>The started child.</returns>
    /// <exception cref="OpenCodeServerException">The process could not be created, placed in the job, resumed, or watched; nothing runs.</exception>
    /// <exception cref="ArgumentException">A command entry or an environment entry holds a NUL.</exception>
    public static async Task<OwnedServerChild> SpawnAsync(ServerChildStart start, LauncherSeams seams)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(seams);

        var request = CreateRequest(start, seams);
        var (spawned, standardOutput) = Launch(request, start, seams);
        return await AdoptAsync(spawned, standardOutput, start, seams).ConfigureAwait(false);
    }

    private static WindowsSpawnRequest CreateRequest(ServerChildStart start, LauncherSeams seams)
    {
        if (NulText.Occurs(start.Executable.Path) ||
            start.SuppliedArguments.Any(NulText.Occurs) ||
            start.LauncherArguments.Any(NulText.Occurs))
        {
            throw new ArgumentException("OpenCodeServerOptions.Command entries cannot contain NUL.", nameof(start));
        }

        var printLogs = seams.HostEnvironment.TryGetValue(PrintLogsVariable, out var value) &&
                        string.Equals(value, "1", StringComparison.Ordinal);
        var command = WindowsCommandLine.For(start.Executable, start.SuppliedArguments, start.LauncherArguments);
        return new WindowsSpawnRequest
        {
            ApplicationPath = command.ApplicationPath,
            CommandLine = command.CommandLine,
            Environment = new ChildEnvironmentComposer(seams.HostEnvironment, StringComparer.OrdinalIgnoreCase).Compose(start.Environment, start.Password),
            WorkingDirectory = ServerChild.WorkingDirectoryOf(start.WorkingDirectory),
            StandardStreams = new WindowsStandardStreams
            {
                Input = ChildStreamRoute.Pipe,
                Output = ChildStreamRoute.Pipe,
                Error = printLogs ? ChildStreamRoute.Inherit : ChildStreamRoute.Pipe,
            },
            StartSuspended = true,
        };
    }

    /// <summary>
    /// Creates the server suspended, places it in the job, and resumes it. All of it runs inside one
    /// <c>finally</c> block, because on .NET Framework a thread abort can land between any two
    /// statements but waits for a <c>finally</c> block to end: an abort can then never leave a
    /// suspended server outside the job, where nothing would ever end it. A step that fails leaves no
    /// one to own the server, so it is ended with code 1 there and every handle and pipe of it is
    /// closed before the start fails. A server still suspended has run no instruction, so it cannot
    /// have started a descendant that ending it would miss.
    /// </summary>
    private static (WindowsSpawnedChild Spawned, Stream StandardOutput) Launch(
        WindowsSpawnRequest request, ServerChildStart start, LauncherSeams seams)
    {
        WindowsSpawnedChild? spawned = null;
        Stream? standardOutput = null;
        ExceptionDispatchInfo? failure = null;
        try
        {
            // Nothing runs here: the work is in the finally block, which a thread abort waits for.
        }
        finally
        {
            try
            {
                spawned = Spawn(request, start, seams.WindowsSpawn);
                standardOutput = StandardOutputOf(spawned);
                PlaceAndResume(spawned, start, seams.Job);
            }
            catch (Exception exception)
            {
                if (spawned is not null)
                {
                    Abandon(spawned);
                }

                failure = ExceptionDispatchInfo.Capture(exception);
            }
        }

        failure?.Throw();
        return spawned is not null && standardOutput is not null
            ? (spawned, standardOutput)
            : throw new InvalidOperationException("The server was neither started nor reported as failed.");
    }

    private static WindowsSpawnedChild Spawn(WindowsSpawnRequest request, ServerChildStart start, IWindowsSpawn spawn)
    {
        try
        {
            return spawn.Spawn(request);
        }
        catch (Win32Exception exception)
        {
            throw new OpenCodeServerException(
                $"Failed to start the server command '{start.Executable.Command}'{ServerChild.DescribeResolution(start.Executable)}{ServerChild.DescribeDirectory(request.WorkingDirectory)}.",
                exception);
        }
    }

    private static Stream StandardOutputOf(WindowsSpawnedChild spawned) =>
        spawned.StandardOutput
        ?? throw new InvalidOperationException("The server's standard output was routed to a pipe, but the spawn returned no read end.");

    /// <summary>Ends a server nobody owns with code 1 and closes every handle and pipe of it.</summary>
    private static void Abandon(WindowsSpawnedChild spawned)
    {
        _ = WindowsInterop.TerminateProcess(spawned.Process, AbandonedExitCode);
        spawned.MainThread?.Dispose();

        // This runs inside the finally block a thread abort must not interrupt, where nothing can
        // be awaited, and closing these pipes does no I/O: none was read, and stdin was never written.
#pragma warning disable MA0045
        spawned.StandardInput?.Dispose();
        spawned.StandardOutput?.Dispose();
        spawned.StandardError?.Dispose();
#pragma warning restore MA0045
        spawned.Process.Dispose();
    }

    /// <summary>
    /// Starts the running server's exit watch and readers. A step that fails leaves no one to own
    /// the server, so it is ended with code 1 here and every handle and pipe of it is closed before
    /// the start fails, and a kernel call that fails here fails the start as an
    /// <see cref="OpenCodeServerException"/>.
    /// </summary>
    private static async Task<OwnedServerChild> AdoptAsync(
        WindowsSpawnedChild spawned, Stream standardOutput, ServerChildStart start, LauncherSeams seams)
    {
        IWindowsExitWatch? watch = null;
        try
        {
            watch = seams.WindowsExits.Watch(spawned.Process);
            var pump = PipeOutputPump.Start(standardOutput, spawned.StandardError, start.OnStandardOutput, start.OnStandardError);
            var ladder = new WindowsServerLadder(spawned.ProcessId, spawned.Process, watch.Exited, seams.TreeKill, WindowsLadderBounds.Default);
            return new OwnedServerChild(
                new OwnedServerProcess
                {
                    ProcessId = spawned.ProcessId,
                    StandardInput = spawned.StandardInput,
                    Output = pump,
                    Exited = watch.Exited,
                    Ladder = ladder,
                    Resources = watch,
                },
                start);
        }
        catch (Exception exception)
        {
            _ = WindowsInterop.TerminateProcess(spawned.Process, AbandonedExitCode);
            await DisposeStreamAsync(spawned.StandardInput).ConfigureAwait(false);
            await DisposeStreamAsync(spawned.StandardOutput).ConfigureAwait(false);
            await DisposeStreamAsync(spawned.StandardError).ConfigureAwait(false);
            if (watch is null)
            {
                spawned.Process.Dispose();
            }
            else
            {
                // The watch owns the handle: it releases it once the ended server is gone.
                watch.Dispose();
            }

            if (exception is Win32Exception kernel)
            {
                throw new OpenCodeServerException(
                    $"The server command '{start.Executable.Command}'{ServerChild.DescribeResolution(start.Executable)} was started, but its exit could not be watched or its output read, so it was ended.",
                    kernel);
            }

            throw;
        }
    }

    /// <summary>
    /// The job assignment and the resume. A refusal with <c>ERROR_ACCESS_DENIED</c> is tolerated, as
    /// libuv tolerates it: the server then runs outside the job, and its stdin lease is what ends it
    /// with its owner. Any other failure, and a job that cannot be created at all, fails the start.
    /// </summary>
    private static void PlaceAndResume(WindowsSpawnedChild spawned, ServerChildStart start, IProcessJob job)
    {
        var thread = spawned.MainThread
            ?? throw new InvalidOperationException("The server was created suspended, but the spawn returned no main thread.");
        try
        {
            _ = job.Assign(spawned.Process);
        }
        catch (Win32Exception exception)
        {
            throw new OpenCodeServerException(
                $"The server command '{start.Executable.Command}'{ServerChild.DescribeResolution(start.Executable)} was created, but it could not be placed in the job that ends it with its owner, so it was ended before it ran.",
                exception);
        }

        if (WindowsInterop.ResumeThread(thread) == ResumeFailed)
        {
            throw new OpenCodeServerException(
                $"The server command '{start.Executable.Command}'{ServerChild.DescribeResolution(start.Executable)} was created suspended, but it could not be resumed, so it was ended before it ran.",
                new Win32Exception(Marshal.GetLastWin32Error()));
        }

        thread.Dispose();
    }

    private static async ValueTask DisposeStreamAsync(Stream? stream)
    {
        if (stream is null)
        {
            return;
        }

        await stream.DisposeAsync().ConfigureAwait(false);
    }
}
