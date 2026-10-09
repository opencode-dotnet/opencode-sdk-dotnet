using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Windows;

namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// Starts the standalone server process a start owns through the platform's strategy: on Windows
/// the launcher creates it suspended, places it in the process-wide job that ends it with its
/// owner, and resumes it (<see cref="WindowsServerChild"/>); on Linux and macOS it spawns it in a
/// session of its own (<see cref="PosixServerChild"/>). Either way the result is the same
/// <see cref="OwnedServerChild"/> flow. Any other platform is refused before anything is spawned.
/// </summary>
internal static class ServerChild
{
    /// <summary>Starts the child for one start.</summary>
    /// <param name="start">What to start.</param>
    /// <param name="seams">The platform seams.</param>
    /// <returns>The started child, whose output is being read.</returns>
    /// <exception cref="OpenCodeServerException">The platform is not supported, or the process could not start; nothing runs.</exception>
    /// <exception cref="ArgumentException">An environment entry or argument holds a NUL, which neither platform's spawn can pass.</exception>
    public static async Task<OwnedServerChild> LaunchAsync(ServerChildStart start, LauncherSeams seams)
    {
        ArgumentNullException.ThrowIfNull(start);
        ArgumentNullException.ThrowIfNull(seams);

        if (WindowsPlatform.IsWindows)
        {
            return await WindowsServerChild.SpawnAsync(start, seams).ConfigureAwait(false);
        }

        if (PosixPlatform.IsLinux || PosixPlatform.IsMacOS)
        {
            return await PosixServerChild.SpawnAsync(start, seams).ConfigureAwait(false);
        }

        throw new OpenCodeServerException(
            "The standalone server is supported on Windows, Linux, and macOS only: this platform has no known way to start it in a session of its own. Nothing was started.");
    }

    /// <summary>Names the resolved target alongside the caller's spelling, when they differ.</summary>
    /// <param name="executable">The resolved command.</param>
    /// <returns>The suffix for a failure message.</returns>
    public static string DescribeResolution(ResolvedExecutable executable)
    {
        ArgumentNullException.ThrowIfNull(executable);
        return string.Equals(executable.Command, executable.Path, StringComparison.Ordinal)
            ? string.Empty
            : $" (resolved to '{executable.Path}')";
    }

    /// <summary>
    /// Names the working directory a failed spawn ran in, when one was set: a missing directory and
    /// a missing executable fail the spawn with the same error, so a failure names both.
    /// </summary>
    /// <param name="workingDirectory">The directory the spawn asked for; null when none.</param>
    /// <returns>The suffix for a failure message.</returns>
    public static string DescribeDirectory(string? workingDirectory) =>
        workingDirectory is { } directory ? $" in the working directory '{directory}'" : string.Empty;

    /// <summary>Reads a blank working directory as none, as <c>Process</c> does.</summary>
    /// <param name="workingDirectory">The caller's directory.</param>
    /// <returns>The directory, or null.</returns>
    public static string? WorkingDirectoryOf(string? workingDirectory) =>
        string.IsNullOrWhiteSpace(workingDirectory) ? null : workingDirectory;
}
