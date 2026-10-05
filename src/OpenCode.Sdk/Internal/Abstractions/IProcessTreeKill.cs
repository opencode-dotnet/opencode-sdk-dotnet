using System.Diagnostics;

namespace OpenCode.Sdk.Internal.Abstractions;

/// <summary>
/// The platform's whole-tree kill, the one primitive the launcher's forced rung issues. Behind a
/// seam so the launcher's handling of a kill the platform reports incomplete is tested against a
/// scripted failure; the shipped implementation is proven against real process trees.
/// </summary>
internal interface IProcessTreeKill
{
    /// <summary>Issues the kill against the process and every descendant the platform reaches.</summary>
    /// <param name="process">The tree's root, started by this process.</param>
    /// <returns><see cref="ProcessTreeKillResult.Issued"/>, or <see cref="ProcessTreeKillResult.Incomplete"/> when the platform's kill reported a failure without throwing.</returns>
    /// <exception cref="InvalidOperationException">The process has already exited, or was never started.</exception>
    /// <exception cref="System.ComponentModel.Win32Exception">The root's handle is no longer accessible.</exception>
    /// <exception cref="AggregateException">At least one process of the tree could not be ended.</exception>
    public ProcessTreeKillResult Kill(Process process);
}
