using System.Collections;
using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Posix.Abstractions;

namespace OpenCode.Sdk.Internal.Launcher;

/// <summary>
/// Everything a standalone start reaches outside the process through: the Windows tree kill, the
/// POSIX spawn, group signal, and exit-status seams, and a snapshot of the host's environment.
/// <see cref="ForCurrentProcess"/> is the public door's set; a test replaces one member.
/// </summary>
internal sealed record LauncherSeams
{
    /// <summary>Gets the Windows tree kill.</summary>
    public required ProcessTreeTerminator TreeTerminator { get; init; }

    /// <summary>Gets the POSIX spawn.</summary>
    public required IPosixSpawn Spawn { get; init; }

    /// <summary>Gets the POSIX group signal.</summary>
    public required IProcessGroupSignal Signals { get; init; }

    /// <summary>Gets the POSIX exit status.</summary>
    public required IChildExitStatus Exits { get; init; }

    /// <summary>Gets the host's environment, which a POSIX child receives under the caller's entries.</summary>
    public required IReadOnlyDictionary<string, string> HostEnvironment { get; init; }

    /// <summary>Creates the shipped seams, with the host's environment as it is now.</summary>
    /// <returns>The seams.</returns>
    public static LauncherSeams ForCurrentProcess() =>
        new()
        {
            TreeTerminator = ProcessTreeTerminator.Platform,
            Spawn = new PosixSpawn(),
            Signals = new ProcessGroupSignal(),
            Exits = new ChildExitStatusReader(),
            HostEnvironment = SnapshotEnvironment(),
        };

    private static Dictionary<string, string> SnapshotEnvironment()
    {
        var snapshot = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (DictionaryEntry entry in Environment.GetEnvironmentVariables())
        {
            if (entry.Key is string key && entry.Value is string value)
            {
                snapshot[key] = value;
            }
        }

        return snapshot;
    }
}
