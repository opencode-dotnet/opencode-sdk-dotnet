using OpenCode.Sdk.Internal.Posix;
using OpenCode.Sdk.Internal.Posix.Abstractions;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The real exit-status seam, reporting that the kernel reaps this process's children itself, as
/// a host with <c>SIGCHLD</c> ignored does. A launcher that honours the report never enters the
/// blocking wait, which could sleep until every child of the host is gone; one that enters it
/// anyway gets an immediate failure, so its exit reads as unknown and the proof sees it.
/// </summary>
internal sealed class ProbeOnlyChildExit : IChildExitStatus
{
    private readonly ChildExitStatusReader _real = new();
    private int _waits;

    /// <summary>Gets how many times the blocking wait was entered.</summary>
    public int Waits => Volatile.Read(ref _waits);

    public bool AreChildrenReapedAutomatically() => true;

    public bool WaitUntilExited(int processId)
    {
        _ = Interlocked.Increment(ref _waits);
        return false;
    }

    public ChildExitStatus? Reap(int processId) => _real.Reap(processId);
}
