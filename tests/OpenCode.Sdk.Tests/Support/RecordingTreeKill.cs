using System.Collections.Concurrent;
using System.Diagnostics;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Windows;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The shipped Windows tree kill behind a recorder: every run is recorded with the time since the
/// recorder was created, and the first <c>idleRuns</c> runs report the tree ended without running
/// the kill at all, the shape of a kill that leaves its root alive, so the ladder's later rungs can
/// be observed against a real process.
/// </summary>
internal sealed class RecordingTreeKill : IWindowsTreeKill
{
    private readonly IWindowsTreeKill _real;
    private readonly int _idleRuns;
    private readonly Stopwatch _clock = Stopwatch.StartNew();
    private readonly ConcurrentQueue<TimeSpan> _runs = new();

    /// <summary>Initializes the recorder over the shipped kill.</summary>
    /// <param name="idleRuns">How many first runs report success without killing anything.</param>
    public RecordingTreeKill(int idleRuns = 0)
    {
        var seams = LauncherSeams.ForCurrentProcess();
        _real = seams.TreeKill;
        _idleRuns = idleRuns;
    }

    /// <summary>Gets when each run started, measured from the recorder's creation.</summary>
    public IReadOnlyList<TimeSpan> Runs => [.. _runs];

    /// <summary>Restarts the clock the runs are measured on.</summary>
    public void Restart() => _clock.Restart();

    public Task<TreeKillOutcome> KillTreeAsync(int processId, TimeSpan bound)
    {
        _runs.Enqueue(_clock.Elapsed);
        return _runs.Count <= _idleRuns
            ? Task.FromResult(TreeKillOutcome.Ended)
            : _real.KillTreeAsync(processId, bound);
    }
}
