namespace OpenCode.Sdk.Internal.Posix.Abstractions;

/// <summary>
/// The exit-status seam: <c>waitid</c> and <c>waitpid</c> on a child of this process, and the
/// <c>SIGCHLD</c> disposition that decides whether the kernel leaves any status to read.
/// </summary>
internal interface IChildExitStatus
{
    /// <summary>
    /// Reads whether the kernel reaps this process's children itself: <c>SIGCHLD</c> ignored, or
    /// its action carrying <c>SA_NOCLDWAIT</c>. A wait then reads no status, and on macOS a waiter
    /// can sleep until every child of the process is gone.
    /// </summary>
    /// <returns>True when children are reaped without a wait.</returns>
    public bool AreChildrenReapedAutomatically();

    /// <summary>
    /// Blocks until the child has exited, without reaping it: <c>waitid(P_PID, pid,
    /// WEXITED | WNOWAIT)</c>, repeated on <c>EINTR</c>. The pid stays the child's until a reap.
    /// </summary>
    /// <param name="processId">The child's pid.</param>
    /// <returns>True once the child has exited; false on any other failure, <c>ECHILD</c> among them (the pid is not, or no longer, an unreaped child of this process).</returns>
    public bool WaitUntilExited(int processId);

    /// <summary>Reaps the child if it has exited: <c>waitpid(pid, WNOHANG)</c>.</summary>
    /// <param name="processId">The child's pid.</param>
    /// <returns>The decoded status; null while the child runs; <see cref="ChildExitStatus.Unknown"/> when the pid is not an unreaped child of this process.</returns>
    public ChildExitStatus? Reap(int processId);
}
