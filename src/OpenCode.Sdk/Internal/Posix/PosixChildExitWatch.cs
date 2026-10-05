using OpenCode.Sdk.Internal.Diagnostics;
using OpenCode.Sdk.Internal.Posix.Abstractions;

namespace OpenCode.Sdk.Internal.Posix;

/// <summary>
/// Observes one spawned child's exit and reaps it, on a background thread of its own. The thread
/// blocks in <c>waitid</c> without reaping, so the pid keeps naming the child until the reap, and
/// then reaps under the lock that <see cref="SignalRootWhileUnreaped"/> takes. That signal path
/// first asks <c>waitpid</c> without hanging whether the pid is still an unreaped child of this
/// process, under the same lock, so a signal sent to the pid through this class never reaches a
/// process that reused it, even when something else reaped the child first. Nothing polls and no
/// process-wide signal handler is installed. The watch reports the child's own exit, never the end
/// of its output, which a descendant can hold open.
/// </summary>
/// <remarks>
/// When the kernel reaps this process's children itself (<c>SIGCHLD</c> ignored, or
/// <c>SA_NOCLDWAIT</c>), a wait reads no status, and on macOS a blocked <c>waitid</c> sleeps until
/// every child of the process is gone. The watch then probes instead, at most
/// <see cref="ProbeInterval"/> apart: <c>kill(pid, 0)</c> reports a child the kernel already reaped
/// as gone, and a <c>waitpid</c> without hanging reaps a zombie that an inherited ignore on macOS
/// still leaves. An exit the watch could not read is reported as
/// <see cref="ChildExitStatus.Unknown"/>, never invented. A runtime that reaps every child (the
/// .NET runtime does when it runs as pid 1) can take the status first; the wait then fails and the
/// exit is unknown as well.
/// </remarks>
internal sealed class PosixChildExitWatch
{
    /// <summary>The longest gap between two probes when the kernel reaps the child itself.</summary>
    private static readonly TimeSpan ProbeInterval = TimeSpan.FromMilliseconds(100);

    /// <summary>Watch threads started in this process and not yet ended.</summary>
    private static int s_liveWatches;

    private readonly int _processId;
    private readonly IChildExitStatus _exits;
    private readonly IProcessGroupSignal _signals;
    private readonly bool _probe;
    private readonly Lock _gate = new();
    private readonly TaskCompletionSource<ChildExitStatus> _exited = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _reaped;

    /// <summary>The status the signal path reaped, which the watch thread then reports.</summary>
    private ChildExitStatus? _reapedBySignalPath;

    private PosixChildExitWatch(int processId, IChildExitStatus exits, IProcessGroupSignal signals, bool probe)
    {
        _processId = processId;
        _exits = exits;
        _signals = signals;
        _probe = probe;
    }

    /// <summary>Gets the child's exit, completed once it has been observed (and, where possible, reaped).</summary>
    public Task<ChildExitStatus> Exited => _exited.Task;

    /// <summary>
    /// Gets how many watch threads started in this process have not ended yet; friend-assembly
    /// test seam for the guard that no watch outlives the test session.
    /// </summary>
    internal static int LiveWatches => Volatile.Read(ref s_liveWatches);

    /// <summary>Starts watching a child this process spawned.</summary>
    /// <param name="processId">The child's pid.</param>
    /// <param name="exits">The exit-status seam.</param>
    /// <param name="signals">The signal seam, for the existence probe.</param>
    /// <param name="childrenReapedAutomatically">Whether the kernel reaps children itself, read before the spawn.</param>
    /// <returns>The running watch.</returns>
    public static PosixChildExitWatch Start(
        int processId, IChildExitStatus exits, IProcessGroupSignal signals, bool childrenReapedAutomatically)
    {
        ArgumentNullException.ThrowIfNull(exits);
        ArgumentNullException.ThrowIfNull(signals);

        var watch = new PosixChildExitWatch(processId, exits, signals, childrenReapedAutomatically);
        var thread = new Thread(watch.Observe) { IsBackground = true, Name = "opencode-server-exit" };
        _ = Interlocked.Increment(ref s_liveWatches);
        try
        {
            thread.Start();
        }
        catch
        {
            _ = Interlocked.Decrement(ref s_liveWatches);
            throw;
        }

        return watch;
    }

    /// <summary>
    /// Signals the child by its pid while the pid still names it: before the reap. After the reap
    /// the pid may belong to another process, and the signal is not sent. A <c>waitpid</c> without
    /// hanging decides it under the lock: none of this process's children has exited under the pid
    /// (the child runs, and the signal is sent); the child had exited and is reaped now (its status
    /// is kept for <see cref="Exited"/>, and nothing is sent); or the pid is no unreaped child of
    /// this process any more, because the kernel or a runtime that reaps every child took it
    /// (nothing is sent).
    /// </summary>
    /// <param name="signal">The signal.</param>
    /// <returns>What the signal achieved; <see cref="SignalDelivery.NoSuchTarget"/> once the child is reaped.</returns>
    public SignalDelivery SignalRootWhileUnreaped(ProcessSignal signal)
    {
        lock (_gate)
        {
            if (_reaped)
            {
                return SignalDelivery.NoSuchTarget;
            }

            if (_exits.Reap(_processId) is { } status)
            {
                _reaped = true;
                _reapedBySignalPath = status;
                return SignalDelivery.NoSuchTarget;
            }

            return _signals.SignalProcess(_processId, signal);
        }
    }

    [SlopwatchSuppress(
        "SW003",
        "Every failure on this thread ends the watch with an unknown exit: an exception escaping a background thread would end the host, and the exit is all a caller is waiting for.")]
    private void Observe()
    {
        var status = ChildExitStatus.Unknown;
        try
        {
            status = _probe ? ProbeUntilGone() : WaitAndReap();
        }
        catch (Exception)
        {
            // A missing export, an unexpected errno, or anything else the native calls raise: the
            // exit could not be read, which Unknown reports.
        }
        finally
        {
            lock (_gate)
            {
                // After a failure the pid can no longer be vouched for, so no later signal uses it.
                _reaped = true;
            }

            // Counted down before completion is signaled, so an owner that awaited Exited already
            // sees this thread gone.
            _ = Interlocked.Decrement(ref s_liveWatches);
            _ = _exited.TrySetResult(status);
        }
    }

    private ChildExitStatus WaitAndReap()
    {
        // A reap by the signal path ends this wait too: the pid is then no child of this process.
        var observed = _exits.WaitUntilExited(_processId);
        lock (_gate)
        {
            if (_reaped)
            {
                return _reapedBySignalPath ?? ChildExitStatus.Unknown;
            }

            _reaped = true;
            return observed ? _exits.Reap(_processId) ?? ChildExitStatus.Unknown : ChildExitStatus.Unknown;
        }
    }

    private ChildExitStatus ProbeUntilGone()
    {
        while (true)
        {
            lock (_gate)
            {
                if (_reaped)
                {
                    return _reapedBySignalPath ?? ChildExitStatus.Unknown;
                }

                if (_signals.ProbeProcess(_processId) is SignalDelivery.NoSuchTarget)
                {
                    _reaped = true;
                    return ChildExitStatus.Unknown;
                }

                // Alive, a zombie no one reaps, or a reused pid: a reap that does not hang tells them apart.
                if (_exits.Reap(_processId) is { } status)
                {
                    _reaped = true;
                    return status;
                }
            }

            Thread.Sleep(ProbeInterval);
        }
    }
}
