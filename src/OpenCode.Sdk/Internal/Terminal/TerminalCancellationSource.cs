namespace OpenCode.Sdk.Internal;

/// <summary>Owns terminal cancellation without blocking a worker while callbacks are scheduled.</summary>
internal sealed class TerminalCancellationSource : IDisposable
{
    private readonly CancellationTokenSource _source = new();

    /// <summary>Gets the token observed by owned I/O or send admission.</summary>
    public CancellationToken Token => _source.Token;

    /// <summary>Requests cancellation and observes all callbacks before completion.</summary>
    /// <remarks>Our lifetime owner prevents source disposal until this task and owned I/O finish.</remarks>
    public Task CancelAsync() => _source.CancelOnWorkerAsync();

    /// <summary>Releases the source after cancellation callbacks and owned I/O have settled.</summary>
    public void Dispose() => _source.Dispose();
}
