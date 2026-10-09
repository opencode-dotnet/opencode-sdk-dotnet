using System.Diagnostics;
using Microsoft.Win32.SafeHandles;
using OpenCode.Sdk.Internal.Launcher.Abstractions;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// How a Windows server and its process tree are ended, on upstream's rungs and on upstream's
/// clock: the grace starts with the first rung and bounds it.
/// <list type="number">
/// <item>A root that already exited is left alone: the tree kill finds only live processes, so on
/// an exited root it reaches none of its descendants and costs a run of a few hundred
/// milliseconds.</item>
/// <item>At once, the tree kill (<c>taskkill /T /F</c>) bounded by the grace; when it fails,
/// <c>TerminateProcess</c> ends the root with code 1, upstream's fallback. A kill the grace
/// overtakes is ended, and the second rung follows at once.</item>
/// <item>Within what is left of the grace, the root's own exit.</item>
/// <item>When the root outlived the grace, the same pair again, the tree kill bounded on its own,
/// unless the root exited meanwhile; then a bounded wait for the root, so disposal never hangs on a
/// process the kernel cannot end.</item>
/// </list>
/// Stdin is not closed first: upstream never closes it in its release, and the server ends by
/// force, as upstream's does. The held process handle keeps the root's pid from naming another
/// process, so neither rung can reach an unrelated one through it.
/// </summary>
internal sealed class WindowsServerLadder : IServerLadder
{
    /// <summary>The exit code the forced fallback gives the root, upstream's own.</summary>
    private const uint ForcedExitCode = 1;

    private readonly int _processId;
    private readonly SafeProcessHandle _process;
    private readonly Task<ChildExitStatus> _exited;
    private readonly IWindowsTreeKill _treeKill;
    private readonly WindowsLadderBounds _bounds;

    /// <summary>Initializes the ladder for one server.</summary>
    /// <param name="processId">The server's pid.</param>
    /// <param name="process">The server's process handle, which the fallback terminates.</param>
    /// <param name="exited">The server's own exit.</param>
    /// <param name="treeKill">The tree kill.</param>
    /// <param name="bounds">The bounds of the second rung; the shipped ones are <see cref="WindowsLadderBounds.Default"/>.</param>
    public WindowsServerLadder(
        int processId, SafeProcessHandle process, Task<ChildExitStatus> exited, IWindowsTreeKill treeKill, WindowsLadderBounds bounds)
    {
        ArgumentNullException.ThrowIfNull(process);
        ArgumentNullException.ThrowIfNull(exited);
        ArgumentNullException.ThrowIfNull(treeKill);
        ArgumentNullException.ThrowIfNull(bounds);

        _processId = processId;
        _process = process;
        _exited = exited;
        _treeKill = treeKill;
        _bounds = bounds;
    }

    /// <summary>Runs the ladder; bounded by the grace, the second tree kill's bound, and the forced-exit wait.</summary>
    /// <param name="grace">The time from the first tree kill to the second.</param>
    /// <returns>A task that completes once the ladder has run to its last applicable rung.</returns>
    public async Task RunAsync(TimeSpan grace)
    {
        if (_exited.IsCompleted)
        {
            return;
        }

        var clock = Stopwatch.StartNew();
        if (grace > TimeSpan.Zero)
        {
            if (await KillTreeWithinAsync(grace).ConfigureAwait(false) is TreeKillOutcome.Failed)
            {
                TerminateRoot();
            }

            if (await RootExitedWithinAsync(grace - clock.Elapsed).ConfigureAwait(false))
            {
                return;
            }
        }

        if (!_exited.IsCompleted &&
            await KillTreeWithinAsync(_bounds.TreeKill).ConfigureAwait(false) is not TreeKillOutcome.Ended)
        {
            TerminateRoot();
        }

        _ = await RootExitedWithinAsync(_bounds.ForcedExit).ConfigureAwait(false);
    }

    /// <summary>
    /// One tree kill, awaited for at most its bound: the kill ends its own run at that bound, and a
    /// kill that does not return by then counts as overtaken, so no rung waits longer than its bound
    /// whatever the kill does.
    /// </summary>
    private async Task<TreeKillOutcome> KillTreeWithinAsync(TimeSpan bound)
    {
        var kill = _treeKill.KillTreeAsync(_processId, bound);
        return await BoundedWait.CompletesWithinAsync(kill, bound).ConfigureAwait(false) && kill.Status is TaskStatus.RanToCompletion
            ? await kill.ConfigureAwait(false)
            : TreeKillOutcome.TimedOut;
    }

    /// <summary>
    /// <c>TerminateProcess</c> on the held handle. A root that exited meanwhile refuses it, which
    /// is the end the fallback asks for, so the result is not read.
    /// </summary>
    private void TerminateRoot() => _ = WindowsInterop.TerminateProcess(_process, ForcedExitCode);

    private Task<bool> RootExitedWithinAsync(TimeSpan window) =>
        BoundedWait.CompletesWithinAsync(_exited, window > TimeSpan.Zero ? window : TimeSpan.Zero);
}
