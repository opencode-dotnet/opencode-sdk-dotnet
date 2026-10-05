#if !NET
using System.ComponentModel;
using System.Diagnostics;
using System.Globalization;
using OpenCode.Sdk.Internal.Diagnostics;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The downlevel Windows tree kill: one bounded run of the system <c>taskkill /pid … /T /F</c>,
/// the reference client's own Windows group kill. taskkill's exit code is the result: zero when
/// every process of the tree was ended, non-zero otherwise — 128 when the root pid is already gone,
/// in which case its descendants were not reached either. A run that outlasts its bound is ended,
/// so no taskkill outlives the disposal that started it.
/// </summary>
internal sealed class TaskkillTreeKill
{
    /// <summary>How long an abandoned taskkill is given to exit after it is ended.</summary>
    private const int EndedKillerExitMilliseconds = 2_000;

    private readonly TimeSpan _bound;

    /// <summary>Initializes a tree kill whose taskkill run is bounded by <paramref name="bound"/>.</summary>
    /// <param name="bound">How long one taskkill run may take before it is abandoned and ended.</param>
    public TaskkillTreeKill(TimeSpan bound) => _bound = bound;

    /// <summary>
    /// Gets the absolute path to the system taskkill.exe, rather than a bare "taskkill" resolved
    /// through PATH: the same binary the reference client invokes, pinned to its well-known system
    /// location.
    /// </summary>
    public static string ExecutablePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");

    /// <summary>Runs taskkill against the tree rooted at <paramref name="processId"/>.</summary>
    /// <param name="processId">The tree's root.</param>
    /// <returns>
    /// <see cref="ProcessTreeKillResult.Issued"/> when taskkill exited zero; otherwise
    /// <see cref="ProcessTreeKillResult.Incomplete"/>: taskkill could not start, exited non-zero, or
    /// did not finish inside the bound.
    /// </returns>
    public ProcessTreeKillResult Kill(int processId)
    {
        Process? killer;
        try
        {
            killer = Process.Start(new ProcessStartInfo
            {
                FileName = ExecutablePath,
                Arguments = "/pid " + processId.ToString(CultureInfo.InvariantCulture) + " /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
        }
        catch (Win32Exception)
        {
            // taskkill could not start, so no kill was issued.
            return ProcessTreeKillResult.Incomplete;
        }

        if (killer is null)
        {
            return ProcessTreeKillResult.Incomplete;
        }

        using (killer)
        {
            if (!killer.WaitForExit(ToMilliseconds(_bound)))
            {
                End(killer);
                return ProcessTreeKillResult.Incomplete;
            }

            return killer.ExitCode is 0 ? ProcessTreeKillResult.Issued : ProcessTreeKillResult.Incomplete;
        }
    }

    private static int ToMilliseconds(TimeSpan bound) =>
        (int)Math.Min(int.MaxValue, Math.Max(0, (long)bound.TotalMilliseconds));

    [SlopwatchSuppress(
        "SW003",
        "Ending the abandoned taskkill is the release itself: a taskkill that exited on its own between the bound and this call is the state the release asks for.")]
    private static void End(Process killer)
    {
        try
        {
            killer.Kill();
            _ = killer.WaitForExit(EndedKillerExitMilliseconds);
        }
        catch (InvalidOperationException)
        {
            // It exited on its own after the bound expired; nothing is left to end.
        }
        catch (Win32Exception)
        {
            // It is already exiting and its handle refuses the kill; it is gone either way.
        }
    }
}
#endif
