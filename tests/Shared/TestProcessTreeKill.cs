using System.ComponentModel;
using System.Diagnostics;
#if !NET
using System.Globalization;
#endif

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// The whole-tree kill test cleanup uses on every OS, reporting rather than raising whatever the
/// kill achieved. The launcher itself ends a server through its own ladder; cleanup ends whatever a
/// test left behind. The modern targets use the runtime's entire-process-tree kill. The downlevel
/// targets, which run on Windows only, run <c>taskkill /T /F</c> from the system folder, bounded.
/// </summary>
internal static class TestProcessTreeKill
{
#if !NET
    /// <summary>How long one cleanup taskkill may run.</summary>
    private const int TaskkillBoundMilliseconds = 10_000;
#endif

    /// <summary>Ends a process tree for cleanup.</summary>
    /// <param name="process">The tree's root.</param>
    /// <returns>True when the kill ran to its end; false when the tree, or part of it, was already gone or refused it.</returns>
    public static bool TryKill(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
        try
        {
#if NET
            process.Kill(entireProcessTree: true);
            return true;
#else
            using var taskkill = Process.Start(new ProcessStartInfo
            {
                FileName = Environment.GetFolderPath(Environment.SpecialFolder.System) + @"\taskkill.exe",
                Arguments = "/pid " + process.Id.ToString(CultureInfo.InvariantCulture) + " /T /F",
                UseShellExecute = false,
                CreateNoWindow = true,
            });
            if (taskkill is null || !taskkill.WaitForExit(TaskkillBoundMilliseconds))
            {
                taskkill?.Kill();
                return false;
            }

            return taskkill.ExitCode is 0;
#endif
        }
        catch (AggregateException)
        {
            // A process of the tree refused the kill; the others were ended.
            return false;
        }
        catch (InvalidOperationException)
        {
            // Already exited: nothing is left to end.
            return false;
        }
        catch (Win32Exception)
        {
            // Exiting, or no longer accessible: nothing further to issue.
            return false;
        }
    }
}
