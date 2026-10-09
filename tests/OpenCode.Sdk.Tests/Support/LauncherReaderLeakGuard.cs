using System.Globalization;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Windows;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The whole-session release guarantee for what the launcher holds per child: every POSIX
/// <see cref="PosixChildExitWatch"/> thread and every Windows <see cref="WindowsExitWatch"/> a test
/// started, directly or through a server it launched, has been released by the time the session
/// ends. Shared server fixtures are disposed after the last test that uses them, before this hook
/// runs, so a watch still live here is a leak: a server a test never disposed or never ended, or a
/// launcher path that does not release what it started. A watch is released once its child exited
/// and its owner let it go. The output readers hold no thread on either platform, and every reader a
/// release ends is awaited by that release.
/// </summary>
public static class LauncherReaderLeakGuard
{
    [After(TestSession)]
    public static void No_Launcher_Watch_Should_Outlive_The_Session()
    {
        var watches = PosixChildExitWatch.LiveWatches;
        if (watches != 0)
        {
            throw new InvalidOperationException(
                watches.ToString(CultureInfo.InvariantCulture)
                + " POSIX exit watch thread(s) are still alive at the end of the test session: a child a test started was never ended, or its exit was never observed.");
        }

        var windowsWatches = WindowsExitWatch.LiveWatches;
        if (windowsWatches != 0)
        {
            throw new InvalidOperationException(
                windowsWatches.ToString(CultureInfo.InvariantCulture)
                + " Windows exit watch(es) are still registered at the end of the test session: a child a test started was never ended, or a launcher path never released its watch.");
        }
    }
}
