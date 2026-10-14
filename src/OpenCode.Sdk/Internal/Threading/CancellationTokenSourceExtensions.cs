namespace OpenCode.Sdk.Internal;

/// <summary>Cancellation that never holds a worker while its callbacks wait to be scheduled.</summary>
internal static class CancellationTokenSourceExtensions
{
    /// <summary>
    /// Requests cancellation with the callbacks run on a pool thread, and completes once they
    /// have all run. On .NET 8 and later this is the runtime's <c>CancelAsync</c>. Below it, the
    /// source-only polyfill's <c>CancelAsync</c> queues <c>Cancel</c> and then spins on the
    /// calling thread until a pool thread starts it: concurrent callers each hold a worker in
    /// that spin, and the queued cancellations wait for workers the spins occupy, so the whole
    /// process stalls until the pool injects threads (measured on net472: timers seconds late
    /// and loopback requests failing their bounds). The polyfill member is banned
    /// (<c>BannedSymbols.txt</c>); this is its replacement, product and tests alike.
    /// </summary>
    public static Task CancelOnWorkerAsync(this CancellationTokenSource source)
    {
        ArgumentNullException.ThrowIfNull(source);
#if NET8_0_OR_GREATER
        return source.CancelAsync();
#else
        return Task.Run(source.Cancel, CancellationToken.None);
#endif
    }
}
