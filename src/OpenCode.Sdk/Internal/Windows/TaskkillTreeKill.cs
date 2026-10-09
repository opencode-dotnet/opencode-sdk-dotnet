using System.ComponentModel;
using System.Globalization;
using OpenCode.Sdk.Internal.Launcher;
using OpenCode.Sdk.Internal.Windows.Abstractions;

namespace OpenCode.Sdk.Internal.Windows;

/// <summary>
/// The shipped <see cref="IWindowsTreeKill"/>: one run of the system <c>taskkill /pid N /T /F</c>,
/// upstream's own Windows group kill, which ends every live descendant by parent pid, detached ones
/// included. It runs from its absolute System32 path, not through <c>PATH</c> and <c>cmd.exe</c>
/// as upstream runs it, so a writable <c>PATH</c> entry cannot substitute the executable. It goes
/// through the SDK's own spawn with no handle at all, so it inherits nothing of the host's, and its
/// exit is awaited through the same registered wait as a server's, so no thread waits for it. A
/// run that outlives its bound is ended, which upstream does not do; nothing it starts outlives
/// the release that started it.
/// </summary>
/// <param name="spawn">The spawn the kill runs through.</param>
/// <param name="exits">The exit watch the kill's own exit is awaited through.</param>
internal sealed class TaskkillTreeKill(IWindowsSpawn spawn, IWindowsChildExit exits) : IWindowsTreeKill
{
    /// <summary>
    /// Gets the absolute path to the system taskkill.exe. The system folder is read from the
    /// operating system rather than from an environment variable, and a 32-bit host on 64-bit
    /// Windows gets the 32-bit tool, which ends 64-bit processes as well.
    /// </summary>
    public static string ExecutablePath =>
        Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.System), "taskkill.exe");

    /// <inheritdoc />
    public async Task<TreeKillOutcome> KillTreeAsync(int processId, TimeSpan bound)
    {
        var path = ExecutablePath;
        WindowsSpawnedChild killer;
        try
        {
            killer = spawn.Spawn(new WindowsSpawnRequest
            {
                ApplicationPath = path,
                CommandLine = "\"" + path + "\" /pid " + processId.ToString(CultureInfo.InvariantCulture) + " /T /F",
            });
        }
        catch (Win32Exception)
        {
            // taskkill could not start, so nothing was ended.
            return TreeKillOutcome.Failed;
        }

        IWindowsExitWatch watch;
        try
        {
            watch = exits.Watch(killer.Process);
        }
        catch (Win32Exception)
        {
            // Nothing can await this run: it is ended, and counts as one that reached nothing.
            _ = WindowsInterop.TerminateProcess(killer.Process, 1);
            killer.Process.Dispose();
            return TreeKillOutcome.Failed;
        }

        using (watch)
        {
            if (!await BoundedWait.CompletesWithinAsync(watch.Exited, bound).ConfigureAwait(false))
            {
                // Ended, not awaited: the watch releases its handle once it is gone, and the
                // caller's own bound is not stretched by the time that takes.
                _ = WindowsInterop.TerminateProcess(killer.Process, 1);
                return TreeKillOutcome.TimedOut;
            }

            return (await watch.Exited.ConfigureAwait(false)).ExitCode is 0 ? TreeKillOutcome.Ended : TreeKillOutcome.Failed;
        }
    }
}
