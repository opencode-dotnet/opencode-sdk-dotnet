using OpenCode.Sdk.Internal.BackgroundService.ProcessControl;
using OpenCode.Sdk.Internal.Posix;
namespace OpenCode.Sdk.Internal.BackgroundService.Abstractions;

/// <summary>
/// The two process primitives the stop lifecycle needs and nothing more: identify a process and
/// signal it. Waiting is the orchestration's job — the pinned client's own schedule over these
/// two. Behind a seam so the orchestration is tested against scripted answers and the shipped
/// implementation is proven against real processes of its own.
/// </summary>
internal interface IServiceProcessControl
{
    /// <summary>
    /// Takes the identity of the process running under a pid right now: null when no process runs
    /// under it, or when its identity cannot be read — the pinned client reads a refused
    /// <c>kill(pid, 0)</c> the same way, as a process that is gone.
    /// </summary>
    /// <param name="processId">The pid.</param>
    /// <returns>The identity, or null.</returns>
    public ProcessIdentity? TrySnapshot(int processId);

    /// <summary>
    /// Sends one rung to exactly the process the identity names: the live identity is compared
    /// immediately before the send, so a pid that was reused since the snapshot is never signalled.
    /// A failed send is reported, never thrown, the way the pinned client ignores its <c>signal</c>
    /// errors.
    /// </summary>
    /// <param name="target">The process to signal.</param>
    /// <param name="signal">The rung.</param>
    /// <returns>True when the signal was sent to that process; false when it was gone, reused, or the send failed.</returns>
    public bool TrySignal(ProcessIdentity target, ProcessSignal signal);
}
