namespace OpenCode.Sdk.Internal;

/// <summary>Holds root admission state and the saved token; Pipeline owns cancellation and disposal.</summary>
internal sealed class ClientLifetime
{
    /// <summary>
    /// Cancelled when the pipeline is disposed, so an HTTP exchange or stream ends at once rather than at
    /// its next read or attempt. The token is kept apart from its source: a stream may still link to
    /// it after the source is disposed, and a cancelled token links as cancelled.
    /// </summary>
    public CancellationToken Token { get; }

    public bool IsDisposed { get; private set; }

    public ClientLifetime(CancellationToken token) => Token = token;

    public void ThrowIfDisposed()
    {
        if (IsDisposed)
        {
            throw FailureClassification.DisposedClient();
        }
    }

    /// <summary>Marks disposal before callbacks run; false leaves cancellation and cleanup untouched.</summary>
    public bool TryBeginDispose()
    {
        if (IsDisposed)
        {
            return false;
        }

        IsDisposed = true;
        return true;
    }
}
