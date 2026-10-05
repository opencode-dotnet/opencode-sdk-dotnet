using System.Diagnostics;
using OpenCode.Sdk.Internal.Abstractions;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The shipped whole-tree kill, which only the Windows launcher issues: on Linux and macOS the
/// launcher ends its server through the server's process group instead. Modern targets use the
/// runtime's entire-process-tree kill, which raises <see cref="AggregateException"/> when any
/// process of the tree could not be ended; net472/netstandard2.0 run <c>taskkill /pid … /T /F</c>
/// (<c>TaskkillTreeKill</c>), the reference client's own Windows group kill. Trap: Polyfill also
/// defines <c>Kill(entireProcessTree)</c> for downlevel targets, but it maps to the plain
/// <c>Kill()</c> — the <c>#if</c> below is what keeps the tree kill real.
/// </summary>
internal sealed class PlatformProcessTreeKill : IProcessTreeKill
{
#if !NET
    /// <summary>How long one taskkill run may take before it is abandoned and ended.</summary>
    private static readonly TimeSpan TaskkillBound = TimeSpan.FromSeconds(10);
#endif

    /// <inheritdoc />
    public ProcessTreeKillResult Kill(Process process)
    {
#if NET
        process.Kill(entireProcessTree: true);
        return ProcessTreeKillResult.Issued;
#else
        return new TaskkillTreeKill(TaskkillBound).Kill(process.Id);
#endif
    }
}
