namespace OpenCode.Sdk.Internal.Windows.Abstractions;

/// <summary>
/// The Windows tree-kill seam: end a process and every live descendant of it at once, the way
/// upstream ends its server on Windows.
/// </summary>
internal interface IWindowsTreeKill
{
    /// <summary>Runs the kill once against the tree rooted at <paramref name="processId"/>, bounded.</summary>
    /// <param name="processId">The tree's root.</param>
    /// <param name="bound">How long the run may take before it is ended.</param>
    /// <returns>How the run ended; never an exception.</returns>
    public Task<TreeKillOutcome> KillTreeAsync(int processId, TimeSpan bound);
}
