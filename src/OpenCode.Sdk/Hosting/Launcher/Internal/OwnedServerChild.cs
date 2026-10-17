using System.Diagnostics;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The standalone server process a start owns, the same on every platform once it runs: how
/// readiness is awaited, how a failed start and a disposal end it through the platform's ladder,
/// and in which order its lease, readers, collector, and handles are released. The platform
/// strategies (<see cref="PosixServerChild"/>, <see cref="WindowsServerChild"/>) differ only in how
/// the process is created, how its exit is observed, and the rungs of the ladder.
/// </summary>
internal sealed class OwnedServerChild : IAsyncDisposable
{
    /// <summary>
    /// How long a failed start waits for the child's output to end after the child exited or
    /// closed its stdout, and how long the failed-start and disposal drains wait at most. The
    /// readiness line was written before the exit, so it is already in the pipe; a descendant that
    /// holds the pipe open costs this bound and no more.
    /// </summary>
    internal static readonly TimeSpan EarlyEndBound = TimeSpan.FromSeconds(1);

    private readonly OwnedServerProcess _process;
    private readonly OpenCodeServerOutput? _output;
    private readonly TimeSpan _grace;
    private readonly TimeProvider _time;
    private readonly Func<Task, CancellationToken, Task>? _readinessWait;
    private Task? _ending;
    private bool _drained;

    /// <summary>Initializes the flow over a running server.</summary>
    /// <param name="process">What the platform strategy started.</param>
    /// <param name="start">The start the server belongs to.</param>
    /// <param name="time">The clock <see cref="EarlyEndBound"/> runs on.</param>
    /// <param name="readinessWait">An optional token-bound wait substitution; null uses <c>Task.WaitAsync</c>.</param>
    public OwnedServerChild(
        OwnedServerProcess process, ServerChildStart start, TimeProvider time,
        Func<Task, CancellationToken, Task>? readinessWait = null)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(time);

        _process = process;
        _output = start.Output;
        _grace = start.GracefulShutdownTimeout;
        _time = time;
        _readinessWait = readinessWait;

        // Started here, so its bound runs from the exit whenever anyone first asks for it.
        Closed = CloseAsync();
    }

    /// <summary>Gets the pid of the process this child owns.</summary>
    public int ProcessId => _process.ProcessId;

    /// <summary>Gets the owned process's own exit, never the end of its output.</summary>
    public Task<ChildExitStatus> Exited => _process.Exited;

    /// <summary>
    /// Gets the owned process's exit once its output ended too, or <see cref="EarlyEndBound"/>
    /// after the exit when a descendant holds a pipe open: upstream's <c>close</c>, which waits for
    /// the exit and stdio and stops waiting for stdio one second after the exit. It drains on its
    /// own after a natural exit, and disposal's release of the readers ends that wait at once.
    /// It never faults.
    /// </summary>
    public Task<ChildExitStatus> Closed { get; }

    /// <summary>Gets a task that completes once no output reader is pending any more.</summary>
    public Task ReadersEnded => _process.Output.ReadersEnded;

    /// <summary>
    /// Waits for the first stdout line until the token fires, or until the child shows it never
    /// will. A child that ended first has been ended and drained by the time this returns.
    /// </summary>
    /// <param name="readyLine">Completes with the first stdout line.</param>
    /// <param name="readiness">Ends the wait: the readiness timeout and the caller's cancellation.</param>
    /// <returns>How the wait ended.</returns>
    /// <exception cref="OperationCanceledException">The token fired first; the caller ends the child.</exception>
    public async Task<ReadinessOutcome> WaitForReadinessAsync(Task<string> readyLine, CancellationToken readiness)
    {
        ArgumentNullException.ThrowIfNull(readyLine);

        // Upstream fails a start when stdout ends, and when the server exits; a server that closes
        // its stdout and keeps running is a failed start as much as one that exits.
        var pending = Task.WhenAny(readyLine, Exited, _process.Output.StandardOutputEnded);
        await (_readinessWait is null ? pending.WaitAsync(readiness) : _readinessWait(pending, readiness)).ConfigureAwait(false);
        var outcome = await ResolveRecordedReadinessAsync(readyLine).ConfigureAwait(false);
        Debug.Assert(outcome is not null, "The readiness wait completed with a recorded outcome.");
#if NET
        return outcome;
#else
        // Downlevel Debug.Assert has no nullable-flow annotation; the assertion establishes the invariant.
        return outcome!;
#endif
    }

    /// <summary>
    /// Resolves a recorded first line, root exit, or stdout end, in that order. An early end drains
    /// once inside its bound and rechecks the line before ending a failed child.
    /// </summary>
    /// <param name="readyLine">Completes with the first stdout line.</param>
    /// <returns>The recorded outcome, or null when none has been recorded.</returns>
    internal async Task<ReadinessOutcome?> ResolveRecordedReadinessAsync(Task<string> readyLine)
    {
        ArgumentNullException.ThrowIfNull(readyLine);

        if (readyLine.IsCompleted)
        {
            return ReadinessOutcome.Ready(await readyLine.ConfigureAwait(false));
        }

        if (!Exited.IsCompleted && !_process.Output.StandardOutputEnded.IsCompleted)
        {
            return null;
        }

        // The exit can win the race against a readiness line still in the pipe, so the output is
        // drained once, inside its bound, before anything is decided; the first line still wins.
        _drained = true;
        _ = await BoundedWait.CompletesWithinAsync(Task.WhenAll(ReadersEnded, Exited), EarlyEndBound, _time).ConfigureAwait(false);
        if (readyLine.IsCompleted)
        {
            // Ready after all. A server that has not exited lives on, so the drain at its end is
            // still to come; one that has exited was drained after its end just now.
            _drained = Exited.IsCompleted;
            return ReadinessOutcome.Ready(await readyLine.ConfigureAwait(false));
        }

        var outcome = Exited.IsCompleted
            ? ReadinessOutcome.Exited(await Exited.ConfigureAwait(false))
            : ReadinessOutcome.OutputClosed;
        await EndOnceAsync().ConfigureAwait(false);
        return outcome;
    }

    /// <summary>
    /// Ends a child that will not become the started server: the ladder with the configured grace,
    /// as upstream's scope close does for a start that failed, unless the readiness wait already
    /// ran it; then one drain inside its bound, unless it already ran, so the stderr tail the
    /// caller is about to quote is as complete as the bound allows.
    /// </summary>
    /// <returns>A task that completes once the child is ended, as far as its bounds allow.</returns>
    public async Task EndFailedStartAsync()
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
    /// <returns>A task that completes once the ladder has run.</returns>
    public async Task EndAsync()
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
    /// once as <see cref="EndAsync"/> does; then closes stdin, releases the readers, releases what
    /// the platform holds, and closes the collection.
    /// </summary>
    /// <returns>A task that completes once everything is released.</returns>
    public async ValueTask DisposeAsync()
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

                try
                {
                    if (_process.StandardInput is { } lease)
                    {
                        await lease.DisposeAsync().ConfigureAwait(false);
                    }

                    await _process.Output.ReleaseAsync(_time).ConfigureAwait(false);
                }
                finally
                {
                    _process.Resources?.Dispose();
                }
            }
        }
        finally
        {
            _output?.Complete();
        }
    }

    /// <summary>
    /// The exit, then the readers' end inside <see cref="EarlyEndBound"/>. It only waits, so it
    /// takes nothing from the drains the readiness wait and disposal run: an expired bound leaves
    /// the readers as they are.
    /// </summary>
    private async Task<ChildExitStatus> CloseAsync()
    {
        var status = await _process.Exited.ConfigureAwait(false);
        _ = await BoundedWait.CompletesWithinAsync(ReadersEnded, EarlyEndBound, _time).ConfigureAwait(false);
        return status;
    }

    /// <summary>The ladder, run at most once, by whichever path ends the child first.</summary>
    private Task EndOnceAsync() => _ending ??= _process.Ladder.RunAsync(_grace);

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
        _ = await _process.Output.DrainAsync(EarlyEndBound, _time).ConfigureAwait(false);
    }
}
