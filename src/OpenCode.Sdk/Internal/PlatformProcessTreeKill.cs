using System.Diagnostics;
using OpenCode.Sdk.Internal.Abstractions;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The shipped whole-tree kill. Modern targets use the runtime's entire-process-tree kill, which
/// raises <see cref="AggregateException"/> when any process of the tree could not be ended;
/// net472/netstandard2.0 on Windows run <c>taskkill /pid … /T /F</c> (<c>TaskkillTreeKill</c>),
/// the reference client's own Windows group kill; downlevel non-Windows kills the root only, and
/// leaves its descendants running. Trap: Polyfill also defines <c>Kill(entireProcessTree)</c> for
/// downlevel targets, but it maps to the plain <c>Kill()</c> — the <c>#if</c> below is what keeps
/// the tree kill real.
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
        if (Environment.OSVersion.Platform == PlatformID.Win32NT)
        {
            return new TaskkillTreeKill(TaskkillBound).Kill(process.Id);
        }

        process.Kill();
        return ProcessTreeKillResult.Issued;
#endif
    }
}
