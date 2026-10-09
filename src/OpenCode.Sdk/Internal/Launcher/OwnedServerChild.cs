using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.Internal.Launcher;

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
    private static readonly TimeSpan EarlyEndBound = TimeSpan.FromSeconds(1);

    private readonly OwnedServerProcess _process;
    private readonly OpenCodeServerOutput? _output;
    private readonly TimeSpan _grace;
    private Task? _ending;
    private bool _drained;

    /// <summary>Initializes the flow over a running server.</summary>
    /// <param name="process">What the platform strategy started.</param>
    /// <param name="start">The start the server belongs to.</param>
    public OwnedServerChild(OwnedServerProcess process, ServerChildStart start)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(start);

        _process = process;
        _output = start.Output;
        _grace = start.GracefulShutdownTimeout;
    }

    /// <summary>Gets the pid of the process this child owns.</summary>
    public int ProcessId => _process.ProcessId;

    /// <summary>Gets the owned process's own exit, never the end of its output.</summary>
    public Task<ChildExitStatus> Exited => _process.Exited;

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
        _ = await Task.WhenAny(readyLine, Exited, _process.Output.StandardOutputEnded).WaitAsync(readiness).ConfigureAwait(false);
        if (readyLine.IsCompleted)
        {
            return ReadinessOutcome.Ready(await readyLine.ConfigureAwait(false));
        }

        // The exit can win the race against a readiness line still in the pipe, so the output is
        // drained once, inside its bound, before anything is decided; the first line still wins.
        _drained = true;
        _ = await BoundedWait.CompletesWithinAsync(Task.WhenAll(ReadersEnded, Exited), EarlyEndBound).ConfigureAwait(false);
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

                    await _process.Output.ReleaseAsync().ConfigureAwait(false);
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
        _ = await _process.Output.DrainAsync(EarlyEndBound).ConfigureAwait(false);
    }
}
