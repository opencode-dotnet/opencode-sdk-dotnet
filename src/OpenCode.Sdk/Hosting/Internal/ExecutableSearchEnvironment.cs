#if !NET
using System.Runtime.InteropServices;
#endif

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The inputs a shell consults to turn a bare command name into something spawnable: the PATH
/// list, the Windows PATHEXT list, the directory relative entries resolve against, the platform
/// dialect, and the existence probe itself. Every one of them is supplied rather than read, so
/// <see cref="ExecutableResolver"/> stays a pure policy both platform dialects can be tested
/// against on whichever host runs the suite. <see cref="ForCurrentProcess"/> is the only place
/// that touches the real environment.
/// </summary>
internal sealed class ExecutableSearchEnvironment
{
    /// <summary>Gets a value indicating whether the Windows dialect applies: <c>;</c>-separated
    /// PATH entries, backslash separators, PATHEXT probing, and batch shims.</summary>
    public required bool IsWindows { get; init; }

    /// <summary>Gets the PATH list; null or empty means nothing is searched.</summary>
    public required string? SearchPath { get; init; }

    /// <summary>Gets the Windows PATHEXT list; null or blank falls back to the conventional list.</summary>
    public required string? SearchExtensions { get; init; }

    /// <summary>Gets the directory a relative PATH entry is resolved against.</summary>
    public required string CurrentDirectory { get; init; }

    /// <summary>Gets the existence probe for a candidate path.</summary>
    public required Func<string, bool> FileExists { get; init; }

    /// <summary>Reads the launching process's own search environment.</summary>
    public static ExecutableSearchEnvironment ForCurrentProcess() =>
        new()
        {
#if NET
            IsWindows = OperatingSystem.IsWindows(),
#else
            IsWindows = RuntimeInformation.IsOSPlatform(OSPlatform.Windows),
#endif
            SearchPath = Environment.GetEnvironmentVariable("PATH"),
            SearchExtensions = Environment.GetEnvironmentVariable("PATHEXT"),
            CurrentDirectory = Directory.GetCurrentDirectory(),
            FileExists = File.Exists,
        };
}
