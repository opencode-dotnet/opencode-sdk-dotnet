using System.Diagnostics;
using OpenCode.Sdk.Internal.Launcher.Abstractions;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Posix.Abstractions;

namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// How a POSIX server and the children in its process group are ended, on upstream's rungs. The
/// server leads its own session, so its pid is its group's id.
/// <list type="number">
/// <item>A root that already exited decides by its status. With a non-zero code and no signal, its
/// group's survivors are ended the same way a live root's group is. With a status nobody could read,
/// the group is probed with <c>kill(-group, 0)</c>, and any member left is ended the same way. With
/// code 0 or on a signal, the ladder stops and the group is left alone. The group's id cannot name
/// another group while it still has a member, so signalling it reaches no unrelated process.</item>
/// <item><c>SIGTERM</c> to the group, falling back to the root's pid. When both fail, nothing is
/// escalated: a target that cannot be signalled is not one a <c>SIGKILL</c> reaches either.</item>
/// <item>Within the grace, the root's exit, then the group's end: <c>kill(-group, 0)</c> until no
/// member is left. A child that detached into a session of its own is not a member and is left to
/// the server, as upstream leaves it.</item>
/// <item><c>SIGKILL</c> to the group (falling back to the pid) when the grace expired with a member
/// left, then a bounded wait for the root, so disposal never hangs on a process the kernel cannot
/// end.</item>
/// </list>
/// </summary>
internal sealed class PosixServerLadder : IServerLadder
{
    /// <summary>How long the last rung waits for the root after <c>SIGKILL</c>.</summary>
    public static readonly TimeSpan ForcedExitTimeout = TimeSpan.FromSeconds(10);

    private static readonly TimeSpan FirstProbeInterval = TimeSpan.FromMilliseconds(10);
    private static readonly TimeSpan LongestProbeInterval = TimeSpan.FromMilliseconds(100);

    private readonly int _processGroupId;
    private readonly PosixChildExitWatch _watch;
    private readonly IProcessGroupSignal _signals;
    private readonly TimeSpan _forcedExitTimeout;

    /// <summary>Initializes the ladder for one server.</summary>
    /// <param name="processGroupId">The server's pid, which names its group.</param>
    /// <param name="watch">The server's exit watch, which also guards the pid fallback.</param>
    /// <param name="signals">The signal seam.</param>
    /// <param name="forcedExitTimeout">How long the last rung waits; the shipped value is <see cref="ForcedExitTimeout"/>.</param>
    public PosixServerLadder(int processGroupId, PosixChildExitWatch watch, IProcessGroupSignal signals, TimeSpan forcedExitTimeout)
    {
        ArgumentNullException.ThrowIfNull(watch);
        ArgumentNullException.ThrowIfNull(signals);

        _processGroupId = processGroupId;
        _watch = watch;
        _signals = signals;
        _forcedExitTimeout = forcedExitTimeout;
    }

    /// <summary>Runs the ladder; bounded by the grace plus the forced-exit wait.</summary>
    /// <param name="grace">The time between <c>SIGTERM</c> and <c>SIGKILL</c>.</param>
    /// <returns>A task that completes once the ladder has run to its last applicable rung.</returns>
    public async Task RunAsync(TimeSpan grace)
    {
        if (_watch.Exited.IsCompleted && !LeavesSurvivorsToEnd(await _watch.Exited.ConfigureAwait(false)))
        {
            return;
        }

        if (!TrySignal(ProcessSignal.Terminate))
        {
            return;
        }

        if (await GroupEndedWithinAsync(grace).ConfigureAwait(false))
        {
            return;
        }

        _ = TrySignal(ProcessSignal.Kill);
        _ = await RootExitedWithinAsync(_forcedExitTimeout).ConfigureAwait(false);
    }

    /// <summary>
    /// Upstream ends the group behind a root that exited on its own only when the exit was a
    /// failure. A status nobody could read decides nothing, so the group itself is asked whether
    /// anything is left of it.
    /// </summary>
    private bool LeavesSurvivorsToEnd(ChildExitStatus status) =>
        status == ChildExitStatus.Unknown
            ? _signals.ProbeGroup(_processGroupId) is not SignalDelivery.NoSuchTarget
            : status is { Signal: null, ExitCode: not null and not 0 };

    private static TimeSpan Shorter(TimeSpan first, TimeSpan second) => first < second ? first : second;

    private bool TrySignal(ProcessSignal signal) =>
        _signals.SignalGroup(_processGroupId, signal) is SignalDelivery.Delivered ||
        _watch.SignalRootWhileUnreaped(signal) is SignalDelivery.Delivered;

    private async Task<bool> GroupEndedWithinAsync(TimeSpan grace)
    {
        var elapsed = Stopwatch.StartNew();
        if (!await RootExitedWithinAsync(grace).ConfigureAwait(false))
        {
            return false;
        }

        var interval = FirstProbeInterval;
        while (_signals.ProbeGroup(_processGroupId) is not SignalDelivery.NoSuchTarget)
        {
            var remaining = grace - elapsed.Elapsed;
            if (remaining <= TimeSpan.Zero)
            {
                return false;
            }

            await Task.Delay(Shorter(interval, remaining)).ConfigureAwait(false);
            interval = Shorter(interval + interval, LongestProbeInterval);
        }

        return true;
    }

    private async Task<bool> RootExitedWithinAsync(TimeSpan window)
    {
        try
        {
            _ = await _watch.Exited.WaitAsync(window).ConfigureAwait(false);
            return true;
        }
        catch (TimeoutException)
        {
            // The window expired with the root still running; the caller decides what follows.
            return false;
        }
    }
}
