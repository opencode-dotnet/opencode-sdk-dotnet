namespace OpenCode.Sdk.Internal;

/// <summary>Serializes physical writes and graceful close behind one connection gate.</summary>
internal sealed class TerminalSendQueue(ITerminalWebSocket socket) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Waits for the next physical-send turn.</summary>
    public Task WaitAsync(CancellationToken cancellationToken) => _gate.WaitAsync(cancellationToken);

    /// <summary>Releases the physical-send turn.</summary>
    public void Release() => _gate.Release();

    /// <summary>Releases the gate after its owner has joined every admitted send and close.</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>Attempts close with separate send-admission and close-output phase budgets.</summary>
    public async Task<bool> TryCloseAsync()
    {
        if (!await _gate.WaitAsync(TerminalSocketBounds.GracefulCloseTimeout, CancellationToken.None).ConfigureAwait(false))
        {
            return false;
        }

        using var timeout = new CancellationTokenSource(TerminalSocketBounds.GracefulCloseTimeout);
        try
        {
            await socket.CloseOutputAsync(timeout.Token).ConfigureAwait(false);
            return true;
        }
        catch (Exception exception) when (
            FailureClassification.Handles(exception, FailurePhase.PtyWebSocketWrite) ||
            exception is InvalidOperationException)
        {
            // Expected graceful-close failures leave unconditional hard teardown to the owner.
            return false;
        }
        finally
        {
            Release();
        }
    }
}
