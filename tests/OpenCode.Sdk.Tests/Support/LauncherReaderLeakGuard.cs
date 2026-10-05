using System.Globalization;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The whole-session release guarantee for the launcher's threads: every Windows
/// <see cref="ChildOutputReader"/> thread and every POSIX <see cref="PosixChildExitWatch"/> thread a
/// test started, directly or through a server it launched, has ended by the time the session ends.
/// Shared server fixtures are disposed after the last test that uses them, before this hook runs,
/// so a thread still alive here is a leak: a server a test never disposed or never ended, or a
/// launcher path that does not release what it started. An exit watch ends when its child does.
/// </summary>
public static class LauncherReaderLeakGuard
{
    [After(TestSession)]
    public static void No_Launcher_Reader_Should_Outlive_The_Session()
    {
        var live = ChildOutputReader.LiveReaders;
        if (live != 0)
        {
            throw new InvalidOperationException(
                live.ToString(CultureInfo.InvariantCulture)
                + " launcher output reader thread(s) are still alive at the end of the test session: a started server was not disposed, or a launcher path did not release its readers.");
        }

        var watches = PosixChildExitWatch.LiveWatches;
        if (watches != 0)
        {
            throw new InvalidOperationException(
                watches.ToString(CultureInfo.InvariantCulture)
                + " POSIX exit watch thread(s) are still alive at the end of the test session: a child a test started was never ended, or its exit was never observed.");
        }
    }
}
