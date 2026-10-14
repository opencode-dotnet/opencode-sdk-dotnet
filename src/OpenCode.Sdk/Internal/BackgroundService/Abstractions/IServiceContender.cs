namespace OpenCode.Sdk.Internal;

/// <summary>
/// One detached Ensure contender as the election loop sees it — the pinned client's
/// <c>ServiceContender</c> (<c>service-contender.ts</c>): whether it finished the way Node's
/// <c>close</c> event means it, whether it exited 0, the failure to report, and the release that
/// gives its retention up. Disposal closes handles and never signals.
/// </summary>
internal interface IServiceContender : IDisposable
{
    /// <summary>Gets the spawned pid; for a Windows batch shim, the cmd.exe host.</summary>
    public int ProcessId { get; }

    /// <summary>
    /// Gets whether the contender finished: an observation error, or its stderr reached
    /// end-of-stream and its exit was observed — upstream's <c>contenderFinished</c>.
    /// </summary>
    public bool Finished { get; }

    /// <summary>Gets whether an exit with code 0 was observed; the election doubles the spawn delay on one.</summary>
    public bool ExitedZero { get; }

    /// <summary>The pinned client's <c>contenderFailure</c>, or null when there is none to report.</summary>
    /// <returns>The failure with the stderr tail, or null.</returns>
    public OpenCodeServerException? TryGetFailure();

    /// <summary>Gives the retention up without signalling; the stderr drain keeps running.</summary>
    public void Release();
}
