using OpenCode.Sdk.Internal.Posix;

namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// The standalone server process a start owns, behind one shape for both platform strategies:
/// how it is created, how readiness is awaited, how it is ended, and how its handles are released.
/// On Windows the child runs through <see cref="System.Diagnostics.Process"/>
/// (<see cref="ProcessServerChild"/>). On Linux and macOS the launcher spawns it in a session of
/// its own and ends it through its process group (<see cref="PosixServerChild"/>). Any other
/// platform is refused before anything is spawned.
/// </summary>
internal abstract class ServerChild : IAsyncDisposable
{
    /// <summary>Gets the pid of the process this child owns.</summary>
    public abstract int ProcessId { get; }

    /// <summary>Gets the owned process's own exit, never the end of its output.</summary>
    public abstract Task<ChildExitStatus> Exited { get; }

    /// <summary>Gets a task that completes once no output reader is pending any more.</summary>
    public abstract Task ReadersEnded { get; }

    /// <summary>Starts the child for one start.</summary>
    /// <param name="start">What to start.</param>
    /// <param name="seams">The platform seams.</param>
    /// <returns>The started child, whose output is being read.</returns>
    /// <exception cref="OpenCodeServerException">The platform is not supported, or the process could not start; nothing runs.</exception>
    /// <exception cref="ArgumentException">An environment entry or argument holds a NUL, which a POSIX spawn cannot pass.</exception>
    public static async Task<ServerChild> LaunchAsync(ServerChildStart start, LauncherSeams seams)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(seams);

        if (LauncherInterop.IsWindows)
        {
            return ProcessServerChild.Launch(start, seams.TreeTerminator);
        }

        if (PosixPlatform.IsLinux || PosixPlatform.IsMacOS)
        {
            return await PosixServerChild.SpawnAsync(start, seams).ConfigureAwait(false);
        }

        throw new OpenCodeServerException(
            "The standalone server is supported on Windows, Linux, and macOS only: this platform has no known way to start it in a session of its own. Nothing was started.");
    }

    /// <summary>
    /// Waits for the first stdout line until the token fires, or until the child shows it never
    /// will. A child that ended first has been ended and drained by the time this returns.
    /// </summary>
    /// <param name="readyLine">Completes with the first stdout line.</param>
    /// <param name="readiness">Ends the wait: the readiness timeout and the caller's cancellation.</param>
    /// <returns>How the wait ended.</returns>
    /// <exception cref="OperationCanceledException">The token fired first; the caller ends the child.</exception>
    public abstract Task<ReadinessOutcome> WaitForReadinessAsync(Task<string> readyLine, CancellationToken readiness);

    /// <summary>
    /// Ends a child that will not become the started server, and drains its output inside a bound,
    /// so the stderr tail the caller is about to quote is as complete as the bound allows.
    /// </summary>
    /// <returns>A task that completes once the child is ended, as far as its bounds allow.</returns>
    public abstract Task EndFailedStartAsync();

    /// <summary>Ends the started server: the platform's disposal ladder, bounded at every step.</summary>
    /// <returns>A task that completes once the ladder has run.</returns>
    public abstract Task EndAsync();

    /// <summary>Releases the readers, the stdin lease, the collector, and the handles; the child is ended first.</summary>
    /// <returns>A task that completes once everything is released.</returns>
    public abstract ValueTask DisposeAsync();

    /// <summary>Names the resolved target alongside the caller's spelling, when they differ.</summary>
    /// <param name="executable">The resolved command.</param>
    /// <returns>The suffix for a failure message.</returns>
    protected static string DescribeResolution(ResolvedExecutable executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        return string.Equals(executable.Command, executable.Path, StringComparison.Ordinal)
            ? string.Empty
            : $" (resolved to '{executable.Path}')";
    }
}
