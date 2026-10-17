namespace OpenCode.Sdk.Internal;

/// <summary>Serializes physical writes and graceful close behind one connection gate.</summary>
internal sealed class TerminalSendQueue(ITerminalWebSocket socket, TimeProvider time) : IDisposable
{
    private readonly SemaphoreSlim _gate = new(1, 1);

    /// <summary>Waits for the next physical-send turn.</summary>
    public Task WaitAsync(CancellationToken cancellationToken) => _gate.WaitAsync(cancellationToken);

    /// <summary>Releases the physical-send turn.</summary>
    public void Release() => _gate.Release();

    /// <summary>Releases the gate after its owner has joined every admitted send and close.</summary>
    public void Dispose() => _gate.Dispose();

    /// <summary>Attempts close with separate send-admission and close-output phase budgets.</summary>
    [SlopwatchSuppress("SW003",
        "Expected graceful-close failure is superseded by the owner's unconditional socket teardown " +
        "and receiver/send joins in DisposeAsync; disposal completion is the observable cleanup outcome.")]
    public async Task CloseAsync()
    {
        using (var admission = TimedCancellation.Create(TerminalSocketBounds.GracefulCloseTimeout, time))
        {
            try
            {
                await _gate.WaitAsync(admission.Token).ConfigureAwait(false);
            }
            catch (OperationCanceledException) when (admission.IsCancellationRequested)
            {
                return;
            }
        }

        try
        {
            using var timeout = TimedCancellation.Create(TerminalSocketBounds.GracefulCloseTimeout, time);
            await socket.CloseOutputAsync(timeout.Token).ConfigureAwait(false);
        }
        catch (Exception exception) when (
            FailureClassification.Handles(exception, FailurePhase.PtyWebSocketWrite) ||
            exception is InvalidOperationException)
        {
            // The owner unconditionally tears down the socket and joins its I/O after this attempt.
        }
        finally
        {
            Release();
        }
    }
}
