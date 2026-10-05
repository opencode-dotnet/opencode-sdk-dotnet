using System.ComponentModel;
using System.Diagnostics;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Posix.Abstractions;

namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// The Linux and macOS strategy. The server is spawned in a session of its own, so a Ctrl+C or a
/// hangup aimed at the host's terminal never reaches it, and its pid names a process group the
/// launcher can signal whole. Its stdin is a pipe the launcher holds and never writes: the
/// ownership lease, whose end-of-stream reaches the server when the host closes it or dies. Its
/// stdout and stderr are pipes read without holding a thread. Its exit is observed by
/// <see cref="PosixChildExitWatch"/>, and it is ended by <see cref="PosixServerLadder"/>.
/// </summary>
/// <remarks>
/// With <c>OPENCODE_PRINT_LOGS=1</c> in the host's environment, as upstream reads it, the server
/// writes its stderr to the host's own stderr instead of a pipe; the startup failure diagnostics
/// then carry no stderr tail and a collector receives no stderr lines.
/// </remarks>
internal sealed class PosixServerChild : ServerChild
{
    /// <summary>Upstream's switch for a server that logs to its owner's stderr.</summary>
    private const string PrintLogsVariable = "OPENCODE_PRINT_LOGS";

    /// <summary>
    /// How long a failed start waits for the child's output to end after the child exited or
    /// closed its stdout, and how long the failed-start and disposal drains wait at most. The
    /// readiness line was written before the exit, so it is already in the pipe; a descendant that
    /// holds the pipe open costs this bound and no more.
    /// </summary>
    private static readonly TimeSpan EarlyEndBound = TimeSpan.FromSeconds(1);

    /// <summary>How often a child that could not be adopted is asked, without hanging, whether it has exited.</summary>
    private static readonly TimeSpan UnadoptedReapInterval = TimeSpan.FromMilliseconds(10);

    /// <summary>How often the background loop asks once the first bound expired with the child still there.</summary>
    private static readonly TimeSpan UnadoptedBackgroundReapInterval = TimeSpan.FromMilliseconds(100);

    private readonly PosixSpawnedChild _spawned;
    private readonly PipeOutputPump _pump;
    private readonly PosixChildExitWatch _watch;
    private readonly PosixServerLadder _ladder;
    private readonly OpenCodeServerOutput? _output;
    private readonly TimeSpan _grace;
    private Task? _ending;
    private bool _drained;

    private PosixServerChild(
        PosixSpawnedChild spawned, PipeOutputPump pump, PosixChildExitWatch watch, PosixServerLadder ladder, ServerChildStart start)
    {
        _spawned = spawned;
        _pump = pump;
        _watch = watch;
        _ladder = ladder;
        _output = start.Output;
        _grace = start.GracefulShutdownTimeout;
    }

    /// <inheritdoc />
    public override int ProcessId => _spawned.ProcessId;

    /// <inheritdoc />
    public override Task<ChildExitStatus> Exited => _watch.Exited;

    /// <inheritdoc />
    public override Task ReadersEnded => _pump.ReadersEnded;

    /// <summary>Spawns the server and starts reading and watching it.</summary>
    /// <param name="start">What to start.</param>
    /// <param name="seams">The POSIX seams and the host's environment.</param>
    /// <returns>The started child.</returns>
    public static async Task<PosixServerChild> SpawnAsync(ServerChildStart start, LauncherSeams seams)
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
        catch (Win32Exception exception)
        {
            throw new OpenCodeServerException(
                $"Failed to start the server command '{start.Executable.Command}'{DescribeResolution(start.Executable)}{DescribeDirectory(request)}.",
                exception);
        }

        return await AdoptAsync(spawned, start, seams, childrenReapedAutomatically).ConfigureAwait(false);
    }

    /// <inheritdoc />
    public override async Task<ReadinessOutcome> WaitForReadinessAsync(Task<string> readyLine, CancellationToken readiness)
    {
        ArgumentNullException.ThrowIfNull(readyLine);

        // Upstream fails a start when stdout ends, and when the server exits; a server that closes
        // its stdout and keeps running is a failed start as much as one that exits.
        _ = await Task.WhenAny(readyLine, _watch.Exited, _pump.StandardOutputEnded).WaitAsync(readiness).ConfigureAwait(false);
        if (readyLine.IsCompleted)
        {
            return ReadinessOutcome.Ready(await readyLine.ConfigureAwait(false));
        }

        // The exit can win the race against a readiness line still in the pipe, so the output is
        // drained once, inside its bound, before anything is decided; the first line still wins.
        _drained = true;
        _ = await BoundedWait.CompletesWithinAsync(Task.WhenAll(_pump.ReadersEnded, _watch.Exited), EarlyEndBound).ConfigureAwait(false);
        if (readyLine.IsCompleted)
        {
            // Ready after all. A server that has not exited lives on, so the drain at its end is
            // still to come; one that has exited was drained after its end just now.
            _drained = _watch.Exited.IsCompleted;
            return ReadinessOutcome.Ready(await readyLine.ConfigureAwait(false));
        }

        var outcome = _watch.Exited.IsCompleted
            ? ReadinessOutcome.Exited(await _watch.Exited.ConfigureAwait(false))
            : ReadinessOutcome.OutputClosed;
        await EndOnceAsync().ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Runs the ladder with the configured grace, as upstream's scope close does for a start that
    /// failed, unless the readiness wait already ran it; then drains once, unless it already did.
    /// </summary>
    /// <inheritdoc />
    public override async Task EndFailedStartAsync()
    {
        await EndOnceAsync().ConfigureAwait(false);
        await DrainOnceAsync().ConfigureAwait(false);
    }

    /// <summary>
    /// The disposal ladder; then, with a collector, one drain of at most
    /// <see cref="EarlyEndBound"/>, after which the collection closes. The readers keep delivering
    /// while the ladder runs, so a server writing while it shuts down never blocks on a full pipe,
    /// and a line the server wrote before it ended reaches the collector even when its reader had
    /// not delivered it yet.
    /// </summary>
    /// <inheritdoc />
    public override async Task EndAsync()
    {
        try
        {
            await EndOnceAsync().ConfigureAwait(false);
        }
        finally
        {
            await CompleteOutputAsync().ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the ladder first when no path has run it yet (a start that an unexpected failure left
    /// before any ending), so no release leaves the server running; then, with a collector, drains
    /// once as <see cref="EndAsync"/> does; then closes stdin, releases the readers, and closes the
    /// collection. The exit watch is left running: it ends, and reaps the child, whenever the
    /// kernel ends the child, which the ladder has asked for.
    /// </summary>
    /// <inheritdoc />
    public override async ValueTask DisposeAsync()
    {
        try
        {
            try
            {
                await EndOnceAsync().ConfigureAwait(false);
            }
            finally
            {
                if (_output is not null)
                {
                    await DrainOnceAsync().ConfigureAwait(false);
                }

                await DisposeStreamAsync(_spawned.StandardInput).ConfigureAwait(false);
                await _pump.ReleaseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            _output?.Complete();
        }
    }

    private static PosixSpawnRequest CreateRequest(ServerChildStart start, LauncherSeams seams)
    {
        var arguments = new List<string>(start.SuppliedArguments.Count + start.LauncherArguments.Count);
        arguments.AddRange(start.SuppliedArguments);
        arguments.AddRange(start.LauncherArguments);
        if (NulText.Occurs(start.Executable.Path) || arguments.Exists(NulText.Occurs))
        {
            throw new ArgumentException("OpenCodeServerOptions.Command entries cannot contain NUL.", nameof(start));
        }

        var printLogs = seams.HostEnvironment.TryGetValue(PrintLogsVariable, out var value) &&
                        string.Equals(value, "1", StringComparison.Ordinal);
        return new PosixSpawnRequest
        {
            ExecutablePath = start.Executable.Path,
            Arguments = arguments,
            Environment = new ChildEnvironmentComposer(seams.HostEnvironment).Compose(start.Environment, start.Password),

            // Blank is no directory, as Process treats it.
            WorkingDirectory = string.IsNullOrWhiteSpace(start.WorkingDirectory) ? null : start.WorkingDirectory,
            StandardInput = ChildStreamRoute.Pipe,
            StandardOutput = ChildStreamRoute.Pipe,
            StandardError = printLogs ? ChildStreamRoute.Inherit : ChildStreamRoute.Pipe,
        };
    }

    /// <summary>
    /// A missing directory and a missing executable fail the spawn with the same errno, so a
    /// failure in a working directory names both.
    /// </summary>
    private static string DescribeDirectory(PosixSpawnRequest request) =>
        request.WorkingDirectory is { } directory ? $" in the working directory '{directory}'" : string.Empty;

    /// <summary>
    /// Starts the readers and the exit watch over a spawned child. If either cannot start, nothing
    /// would own the child, so it is killed with its group and its pipes are closed here. The child
    /// has one reaper: the watch, once it started; otherwise this path. It asks <c>waitpid</c>
    /// without hanging for at most <see cref="PosixServerLadder.ForcedExitTimeout"/>, and then
    /// leaves the asking to a background loop that holds no thread, so a child the kernel cannot
    /// end at once never hangs the start.
    /// </summary>
    private static async Task<PosixServerChild> AdoptAsync(
        PosixSpawnedChild spawned, ServerChildStart start, LauncherSeams seams, bool childrenReapedAutomatically)
    {
        PosixChildExitWatch? watch = null;
        try
        {
            var standardOutput = spawned.StandardOutput
                ?? throw new InvalidOperationException("The server's standard output was routed to a pipe, but the spawn returned no read end.");
            var pump = PipeOutputPump.Start(standardOutput, spawned.StandardError, start.OnStandardOutput, start.OnStandardError);
            watch = PosixChildExitWatch.Start(spawned.ProcessId, seams.Exits, seams.Signals, childrenReapedAutomatically);
            var ladder = new PosixServerLadder(spawned.ProcessId, watch, seams.Signals, PosixServerLadder.ForcedExitTimeout);
            return new PosixServerChild(spawned, pump, watch, ladder, start);
        }
        catch
        {
            _ = seams.Signals.SignalGroup(spawned.ProcessId, ProcessSignal.Kill);

            // With a watch running, it reaps the child once the kill ends it, as its only reaper.
            if (watch is null &&
                !await ReapWithinAsync(spawned.ProcessId, seams.Exits, PosixServerLadder.ForcedExitTimeout, UnadoptedReapInterval).ConfigureAwait(false))
            {
                _ = ReapWithinAsync(spawned.ProcessId, seams.Exits, TimeSpan.MaxValue, UnadoptedBackgroundReapInterval)
                    .ContinueWith(
                        static reaping => _ = reaping.Exception,
                        CancellationToken.None,
                        TaskContinuationOptions.OnlyOnFaulted | TaskContinuationOptions.ExecuteSynchronously,
                        TaskScheduler.Default);
            }

            await DisposeStreamAsync(spawned.StandardInput).ConfigureAwait(false);
            await DisposeStreamAsync(spawned.StandardOutput).ConfigureAwait(false);
            await DisposeStreamAsync(spawned.StandardError).ConfigureAwait(false);
            throw;
        }
    }

    /// <summary>
    /// Reaps a child no watch owns, asking <c>waitpid</c> without hanging every
    /// <paramref name="interval"/>. A child the kernel already reaped, or a pid that is no child of
    /// this process any more, ends the asking as well.
    /// </summary>
    /// <returns>True once the child is reaped or gone; false when the bound expired first.</returns>
    private static async Task<bool> ReapWithinAsync(int processId, IChildExitStatus exits, TimeSpan bound, TimeSpan interval)
    {
        var elapsed = Stopwatch.StartNew();
        while (exits.Reap(processId) is null)
        {
            if (elapsed.Elapsed >= bound)
            {
                return false;
            }

            await Task.Delay(interval).ConfigureAwait(false);
        }

        return true;
    }

    private static async ValueTask DisposeStreamAsync(Stream? stream)
    {
        if (stream is null)
        {
            return;
        }

        await stream.DisposeAsync().ConfigureAwait(false);
    }

    /// <summary>The ladder, run at most once, by whichever path ends the child first.</summary>
    private Task EndOnceAsync() => _ending ??= _ladder.RunAsync(_grace);

    /// <summary>
    /// With a collector, drains once and then closes the collection. Without one nobody reads the
    /// lines the ended server left in its pipes, so nothing waits for them.
    /// </summary>
    private async Task CompleteOutputAsync()
    {
        if (_output is null)
        {
            return;
        }

        await DrainOnceAsync().ConfigureAwait(false);
        _output.Complete();
    }

    /// <summary>
    /// Waits, inside <see cref="EarlyEndBound"/>, for the readers to deliver what the ended server
    /// wrote, unless a path already drained. A descendant left holding a pipe costs that bound and
    /// no more.
    /// </summary>
    private async Task DrainOnceAsync()
    {
        if (_drained)
        {
            return;
        }

        _drained = true;
        _ = await _pump.DrainAsync(EarlyEndBound).ConfigureAwait(false);
    }
}
