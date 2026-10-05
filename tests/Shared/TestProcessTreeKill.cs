using System.Diagnostics;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.Abstractions;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// The whole-tree kill test cleanup uses on every OS. On Windows it is the launcher's own kill, so
/// cleanup ends a tree the way the product does. On Linux and macOS the launcher ends its server
/// through the server's process group and never kills a tree, so cleanup carries its own: the
/// runtime's entire-process-tree kill on the modern targets, and on the downlevel targets, which
/// have none, a kill of the root alone.
/// </summary>
internal sealed class TestProcessTreeKill : IProcessTreeKill
{
    private static readonly ProcessTreeTerminator Terminator =
        OperatingSystem.IsWindows() ? ProcessTreeTerminator.Platform : new ProcessTreeTerminator(new TestProcessTreeKill());

    /// <summary>Ends a process tree for cleanup, reporting rather than raising whatever the kill achieved.</summary>
    /// <param name="process">The tree's root.</param>
    /// <returns>What the kill achieved.</returns>
    public static ProcessTreeKillResult TryKill(Process process) => Terminator.TryKill(process);

    /// <inheritdoc />
    public ProcessTreeKillResult Kill(Process process)
    {
        ArgumentNullException.ThrowIfNull(process);
#if NET
        process.Kill(entireProcessTree: true);
#else
        process.Kill();
#endif
        return ProcessTreeKillResult.Issued;
    }
}
