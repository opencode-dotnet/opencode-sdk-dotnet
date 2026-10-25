using System.ComponentModel;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The Linux and macOS strategy. The server is spawned in a session of its own, so a Ctrl+C or a
/// hangup aimed at the host's terminal never reaches it, and its pid names a process group the
/// launcher can signal whole. Its stdin is a pipe the launcher holds and never writes: the
/// ownership lease, whose end-of-stream reaches the server when the host closes it or dies. Its
/// stdout and stderr are pipes read without holding a thread. Its exit is observed by
/// <see cref="PosixChildExitWatch"/>, and it is ended by <see cref="PosixServerLadder"/>; the
/// readiness, end, and release flow is the shared <see cref="OwnedServerChild"/>.
/// </summary>
/// <remarks>
/// With <c>OPENCODE_PRINT_LOGS=1</c> in the host's environment, as upstream reads it, the server
/// writes its stderr to the host's own stderr instead of a pipe; the startup failure diagnostics
/// then carry no stderr tail and a collector receives no stderr lines. The exit watch is left
/// running at release: it ends, and reaps the child, whenever the kernel ends the child, which the
/// ladder has asked for.
/// </remarks>
internal static class PosixServerChild
{
    /// <summary>Upstream's switch for a server that logs to its owner's stderr.</summary>
    private const string PrintLogsVariable = "OPENCODE_PRINT_LOGS";

    /// <summary>How often a child that could not be adopted is asked, without hanging, whether it has exited.</summary>
    private static readonly TimeSpan UnadoptedReapInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>How often the background loop asks once the first bound expired with the child still there.</summary>
    private static readonly TimeSpan UnadoptedBackgroundReapInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Spawns the server and starts reading and watching it.</summary>
    /// <param name="start">What to start.</param>
    /// <param name="seams">The POSIX seams and the host's environment.</param>
    /// <returns>The started child.</returns>
    public static async Task<OwnedServerChild> SpawnAsync(ServerChildStart start, LauncherSeams seams)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(seams);

        var request = CreateRequest(start, seams);

        // Read before the spawn: once the kernel reaps children itself, the child's exit has to be
        // probed for rather than waited on.
        var childrenReapedAutomatically = seams.Exits.AreChildrenReapedAutomatically();
        PosixSpawnedChild spawned;
        try
        {
            spawned = seams.Spawn.Spawn(request);
        }
        catch (PlatformNotSupportedException exception)
        {
            throw new OpenCodeServerException(
                "The standalone server needs a session of its own, which this platform cannot provide through posix_spawn. Nothing was started.",
                exception);
        }
        catch (Exception exception) when (exception is Win32Exception or IOException)
        {
            throw new OpenCodeServerException(
                $"Failed to start the server command '{start.Executable.Command}'{ServerChild.DescribeResolution(start.Executable)}{ServerChild.DescribeDirectory(request.WorkingDirectory)}.",
                exception);
        }

        return await AdoptAsync(spawned, start, seams, childrenReapedAutomatically).ConfigureAwait(false);
    }

    private static PosixSpawnRequest CreateRequest(ServerChildStart start, LauncherSeams seams)
    {
        var arguments = new List<string>(start.SuppliedArguments.Count + start.LauncherArguments.Count);
        arguments.AddRange(start.SuppliedArguments);
        arguments.AddRange(start.LauncherArguments);
        var printLogs = seams.HostEnvironment.TryGetValue(PrintLogsVariable, out var value) &&
                        string.Equals(value, "1", StringComparison.Ordinal);
        return new PosixSpawnRequest
        {
            ExecutablePath = start.Executable.Path,
            Arguments = arguments,
            Environment = new ChildEnvironmentComposer(seams.HostEnvironment, StringComparer.Ordinal).Compose(start.Environment, start.Password),
            WorkingDirectory = ServerChild.WorkingDirectoryOf(start.WorkingDirectory),
            StandardInput = ChildStreamRoute.Pipe,
            StandardOutput = ChildStreamRoute.Pipe,
            StandardError = printLogs ? ChildStreamRoute.Inherit : ChildStreamRoute.Pipe,
        };
    }

    /// <summary>
    /// Starts the readers and the exit watch over a spawned child. If either cannot start, nothing
    /// would own the child, so it is killed with its group and its pipes are closed here. The child
    /// has one reaper: the watch, once it started; otherwise this path. It asks <c>waitpid</c>
    /// without hanging for at most <see cref="PosixServerLadder.ForcedExitTimeout"/>, and then
    /// leaves the asking to a background loop that holds no thread, so a child the kernel cannot
    /// end at once never hangs the start.
    /// </summary>
    private static async Task<OwnedServerChild> AdoptAsync(
        PosixSpawnedChild spawned, ServerChildStart start, LauncherSeams seams, bool childrenReapedAutomatically)
    {
        PosixChildExitWatch? watch = null;
        try
        {
            var standardOutput = spawned.StandardOutput
                ?? throw new InvalidOperationException("The server's standard output was routed to a pipe, but the spawn returned no read end.");
            var pump = PipeOutputPump.Start(standardOutput, spawned.StandardError, start.OnStandardOutput, start.OnStandardError);
            watch = PosixChildExitWatch.Start(spawned.ProcessId, seams.Exits, seams.Signals, childrenReapedAutomatically);
            var ladder = new PosixServerLadder(spawned.ProcessId, watch, seams.Signals, PosixServerLadder.ForcedExitTimeout, seams.Time);
            return new OwnedServerChild(
                new OwnedServerProcess
                {
                    ProcessId = spawned.ProcessId,
                    StandardInput = spawned.StandardInput,
                    Output = pump,
                    Exited = watch.Exited,
                    Ladder = ladder,
                },
                start,
                seams.Time,
                seams.ReadinessWait);
        }
        catch
        {
            try
            {
                _ = seams.Signals.SignalGroup(spawned.ProcessId, ProcessSignal.Kill);

                // With a watch running, it reaps the child once the kill ends it, as its only reaper.
                if (watch is null &&
                    await ReapWithinAsync(spawned.ProcessId, seams, PosixServerLadder.ForcedExitTimeout, UnadoptedReapInterval).ConfigureAwait(false) is null)
                {
                    // No watch owns this child. The loop reaps it, or ends with an unknown exit on
                    // an unexpected fault; the failed start has no remaining caller for that fault.
                    _ = ReapWithinAsync(spawned.ProcessId, seams, TimeSpan.MaxValue, UnadoptedBackgroundReapInterval);
                }
            }
            finally
            {
                await DisposeStreamAsync(spawned.StandardInput).ConfigureAwait(false);
                await DisposeStreamAsync(spawned.StandardOutput).ConfigureAwait(false);
                await DisposeStreamAsync(spawned.StandardError).ConfigureAwait(false);
            }
            throw;
        }
    }

    /// <summary>
    /// Reaps a child no watch owns, asking <c>waitpid</c> without hanging every
    /// <paramref name="interval"/>. A child the kernel already reaped, or a pid that is no child of
    /// this process any more, ends the asking as well.
    /// </summary>
    /// <returns>The child's exit, unknown on a background fault, or null when the foreground bound expired first.</returns>
    [SlopwatchSuppress(
        "SW003",
        "The failed start already threw to its caller, no caller remains to receive a reap fault, and the system reaps an orphaned child when the host exits.")]
    private static async Task<ChildExitStatus?> ReapWithinAsync(int processId, LauncherSeams seams, TimeSpan bound, TimeSpan interval)
    {
        try
        {
            var started = seams.Time.GetTimestamp();
            while (true)
            {
                if (seams.Exits.Reap(processId) is { } status)
                {
                    return status;
                }

                if (seams.Time.GetElapsedTime(started) >= bound)
                {
                    return null;
                }

#if NET
                await Task.Delay(interval, seams.Time).ConfigureAwait(false);
#else
                await seams.Time.Delay(interval).ConfigureAwait(false);
#endif
            }
        }
        catch (Exception) when (bound == TimeSpan.MaxValue)
        {
            return ChildExitStatus.Unknown;
        }
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
