using System.IO.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// The machine-wide configuration an opencode server reads besides every root the isolation
/// moves: upstream's system directory (<c>packages/core/src/config/discovery.ts:28-32</c> at the
/// pin) and, on macOS, the MDM managed-preference plists, the first one present winning
/// (<c>discovery.ts:35-40</c>). They load last, as managed settings, and override every other
/// source, the isolation's own empty config seed included (<c>packages/core/src/config.ts:244-290</c>).
/// On Windows the directory follows <c>ProgramData</c>, which <see cref="ServerIsolation"/> moves
/// into each run root. On Linux and macOS no variable moves any of them, so an owned launch refuses
/// to start while one exists rather than run against it.
/// </summary>
internal static class SystemConfigurationSources
{
    /// <summary>The run-root directory an owned Windows server takes as its <c>ProgramData</c>.</summary>
    /// <param name="fileSystem">The filesystem the run root lives on.</param>
    /// <param name="runRoot">The server's run root.</param>
    /// <returns>The directory, empty, so the server's system directory beneath it does not exist.</returns>
    public static string ProgramDataFor(IFileSystem fileSystem, string runRoot) =>
        fileSystem.Path.Combine(runRoot, "programdata");

    /// <summary>The sources no variable moves, as upstream names them on Linux or macOS.</summary>
    /// <param name="macOS">Whether the sources are macOS's rather than Linux's.</param>
    /// <param name="userName">The user name the per-user managed-preference plist is under.</param>
    /// <returns>The paths, in upstream's order.</returns>
    public static IReadOnlyList<string> Unmovable(bool macOS, string userName) =>
        macOS
            ?
            [
                "/Library/Application Support/opencode",
                "/Library/Managed Preferences/" + userName + "/ai.opencode.managed.plist",
                "/Library/Managed Preferences/ai.opencode.managed.plist",
            ]
            : ["/etc/opencode"];

    /// <summary>Finds the first source that exists, as a directory or a file.</summary>
    /// <param name="fileSystem">The filesystem to look on.</param>
    /// <param name="candidates">The sources.</param>
    /// <returns>The first one present, or null.</returns>
    public static string? FirstPresent(IFileSystem fileSystem, IEnumerable<string> candidates) =>
        candidates.FirstOrDefault(path => fileSystem.Directory.Exists(path) || fileSystem.File.Exists(path));

    /// <summary>Refuses an owned launch on Linux or macOS while a source no variable moves exists.</summary>
    /// <param name="fileSystem">The filesystem to look on.</param>
    /// <exception cref="InvalidOperationException">A source exists.</exception>
    public static void RefuseUnmovable(IFileSystem fileSystem)
    {
        if (OperatingSystem.IsWindows())
        {
            return;
        }

        var macOS = OperatingSystem.IsMacOS();
        if (FirstPresent(fileSystem, Unmovable(macOS, Environment.UserName)) is { } present)
        {
            throw new InvalidOperationException(
                $"An owned test server would load '{present}': opencode reads it as managed settings, above every "
                + "source the isolation sets, and no variable moves it on " + (macOS ? "macOS" : "Linux") + ". "
                + "Move it aside while the tests run.");
        }
    }
}
