using System.Net.WebSockets;
using System.Runtime.CompilerServices;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The family-neutral WebSocket lifecycle both terminal sessions share: receive with fragment
/// reassembly, serialized sends, a bounded graceful close, idempotent disposal, and one active
/// read enumeration. What differs between families — how a message decodes, what a close status
/// means — rides the two seams; the owner type names the failures.
/// </summary>
/// <typeparam name="TFrame">The frame type the owning family's read enumeration yields.</typeparam>
internal sealed class TerminalSocketCore<TFrame> : IAsyncDisposable
    where TFrame : class
{
    private readonly Lock _lifecycle = new();
    private readonly TerminalReceivePump<TFrame> _receiver;
    private readonly Type _owner;
    private readonly TerminalCancellationSource _sendEnded = new();
    private readonly CancellationToken _sendEndedToken;
    private readonly TerminalSendQueue _sendGate;
    private readonly ITerminalWebSocket _socket;
    private int _disposed;
    private int _reading;
    private int _socketDisposed;
    private Task? _disposal;
    private TaskCompletionSource<bool>? _sendsDrained;
    private int _activeSends;

    public TerminalSocketCore(
        ITerminalWebSocket socket,
        ITerminalFrameDecoder<TFrame> decoder,
        ITerminalClosePolicy closePolicy,
        Type owner)
    {
        ArgumentNullException.ThrowIfNull(socket);
        ArgumentNullException.ThrowIfNull(decoder);
        ArgumentNullException.ThrowIfNull(closePolicy);
        ArgumentNullException.ThrowIfNull(owner);

        _socket = socket;
        _sendGate = new TerminalSendQueue(socket);
        _owner = owner;
        _sendEndedToken = _sendEnded.Token;
        _receiver = new TerminalReceivePump<TFrame>(socket, decoder, closePolicy, _sendEnded.CancelAsync);
    }

    /// <summary>Gets whether the core has been disposed.</summary>
    public bool IsDisposed => Volatile.Read(ref _disposed) is 1;

    /// <summary>Gets the fixed per-send budget captured by the connection.</summary>
    public TimeSpan SendTimeout { get; init; } = TerminalSocketBounds.DefaultSendTimeout;

    /// <summary>Gets the timer boundary used for per-send deadlines.</summary>
    public ITerminalDeadlineFactory DeadlineFactory { get; init; } = TerminalDeadlineFactory.Instance;

    /// <summary>
    /// Reads the frames the server sends until it closes the connection normally. One core
    /// carries one active enumeration: undelivered frames have one consumer, so a second
    /// concurrent enumeration is refused. Reading after disposal is not an error: unlike
    /// <see cref="SendAsync(ArraySegment{byte}, WebSocketMessageType, CancellationToken)"/>,
    /// which throws once disposed, the enumeration simply ends empty.
    /// </summary>
    /// <param name="cancellationToken">The cancellation token ending the read.</param>
    /// <returns>The frames, in the order the server sent them.</returns>
    public IAsyncEnumerable<TFrame> ReadAsync(CancellationToken cancellationToken) =>
        ReadCoreAsync(cancellationToken);

    /// <summary>
    /// Sends one complete message of the given type. Sends are serialized: the socket allows one
    /// outstanding send, so concurrent callers queue rather than corrupt the stream.
    /// </summary>
    /// <param name="payload">The bytes to send.</param>
    /// <param name="messageType">The message type to send them as.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the message is sent.</returns>
    public Task SendAsync(
        ArraySegment<byte> payload,
        WebSocketMessageType messageType,
        CancellationToken cancellationToken) => SendAsync(() => payload, messageType, null, cancellationToken);

    /// <summary>Sends a message with family-owned preparation and publication under the same gate.</summary>
    /// <param name="createPayload">Copies or encodes the owned message before the first asynchronous wait.</param>
    /// <param name="messageType">The message's wire type.</param>
    /// <param name="actions">The work that shares the message's send order.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes after the send and its successful publication.</returns>
    public async Task SendAsync(
        Func<ArraySegment<byte>> createPayload,
        WebSocketMessageType messageType,
        TerminalSendActions? actions,
        CancellationToken cancellationToken)
    {
        lock (_lifecycle)
        {
            ObjectDisposedException.ThrowIf(IsDisposed, _owner);
            ThrowIfSendFailed();
            if (_activeSends++ is 0)
            {
                _sendsDrained = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            }
        }

        try
        {
            await SendCoreAsync(createPayload, messageType, actions, cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            lock (_lifecycle)
            {
                if (--_activeSends is 0)
                {
                    _ = _sendsDrained!.TrySetResult(true);
                }
            }
        }
    }

    private async Task SendCoreAsync(
        Func<ArraySegment<byte>> createPayload,
        WebSocketMessageType messageType,
        TerminalSendActions? actions,
        CancellationToken cancellationToken)
    {

        using var deadline = new TerminalSendDeadline(_owner, SendTimeout, DeadlineFactory, cancellationToken);
        var payload = createPayload();
        using var admission = CancellationTokenSource.CreateLinkedTokenSource(deadline.Token, _sendEndedToken);
        try
        {
            await _sendGate.WaitAsync(admission.Token).ConfigureAwait(false);
        }
        catch (OperationCanceledException exception)
        {
            throw deadline.Map(exception, queued: true, IsDisposed, _receiver.Outcome);
        }

        try
        {
            // Re-checked behind the gate: a disposal can land while a queued send waits. It sits
            // outside the mapping block deliberately — ObjectDisposedException is in the write
            // phase's fault set, so a refusal raised inside it would be remapped into a transport
            // failure instead of reaching the caller as the misuse it is.
            ObjectDisposedException.ThrowIf(IsDisposed, _owner);
            ThrowIfSendFailed();
            try
            {
                deadline.Token.ThrowIfCancellationRequested();
            }
            catch (OperationCanceledException exception)
            {
                throw deadline.Map(exception, queued: true, IsDisposed, _receiver.Outcome);
            }

            actions?.Prepare?.Invoke();

            try
            {
                await _socket.SendAsync(payload, messageType, deadline.Token).ConfigureAwait(false);
            }
            catch (Exception exception) when (FailureClassification.Handles(exception, FailurePhase.PtyWebSocketWrite))
            {
                var failure = deadline.Map(exception, queued: false, IsDisposed, _receiver.Outcome);
                await FailSendAsync(failure).ConfigureAwait(false);
                throw failure;
            }

            actions?.OnSent?.Invoke();
        }
        finally
        {
            _sendGate.Release();
        }
    }

    /// <summary>
    /// Closes the connection: a graceful close first, bounded so an unresponsive peer cannot
    /// stall the caller, then the socket's hard teardown. Disposal is idempotent, and a read
    /// waiting on the socket ends as a normal end rather than a fault.
    /// </summary>
    /// <returns>A task that completes once the connection is closed.</returns>
    public ValueTask DisposeAsync()
    {
        lock (_lifecycle)
        {
            if (_disposal is null)
            {
                Volatile.Write(ref _disposed, 1);
                _receiver.Abandon();
                _disposal = DisposeCoreAsync();
            }

            return new ValueTask(_disposal);
        }
    }

    private async Task DisposeCoreAsync()
    {
        await _sendEnded.CancelAsync().ConfigureAwait(false);

        try
        {
            if (Volatile.Read(ref _socketDisposed) is 0)
            {
                _ = await _sendGate.TryCloseAsync().ConfigureAwait(false);
            }
        }
        finally
        {
            // Unconditional: whatever the graceful close does, a disposal that has already
            // latched _disposed must never leave the socket alive behind it.
            try
            {
                DisposeSocket();
            }
            finally
            {
                try
                {
                    await _receiver.DisposeAsync().ConfigureAwait(false);
                }
                finally
                {
                    await AwaitSendsAsync().ConfigureAwait(false);
                    _sendGate.Dispose();
                    _sendEnded.Dispose();
                }
            }
        }

    }

    private Task AwaitSendsAsync()
    {
        lock (_lifecycle)
        {
            return _sendsDrained?.Task ?? Task.CompletedTask;
        }
    }

    private void ThrowIfSendFailed()
    {
        if (_receiver.Outcome is { } outcome)
        {
            throw outcome.SendFailure;
        }
    }

    private async Task FailSendAsync(Exception failure)
    {
        var terminalFailure = failure as OpenCodeTransportException ?? new OpenCodeTransportException(
            "The opencode PTY WebSocket was interrupted during a send; delivery is uncertain.", failure);
        _receiver.Complete(terminalFailure);
        await _sendEnded.CancelAsync().ConfigureAwait(false);
        DisposeSocket();
    }

    private void DisposeSocket()
    {
        if (Interlocked.Exchange(ref _socketDisposed, 1) is 0)
        {
            _socket.Dispose();
        }
    }

    private async IAsyncEnumerable<TFrame> ReadCoreAsync(
        [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        if (IsDisposed)
        {
            yield break;
        }

        if (Interlocked.Exchange(ref _reading, 1) is 1)
        {
            throw new InvalidOperationException(
                $"A '{_owner.Name}' carries one active read enumeration; undelivered frames have one consumer.");
        }

        try
        {
            while (true)
            {
                if (_receiver.TryRead(cancellationToken, out var frame))
                {
                    yield return frame;
                    continue;
                }

                if (!await _receiver.WaitToReadAsync(cancellationToken).ConfigureAwait(false))
                {
                    if (_receiver.Failure is { } failure)
                    {
                        throw failure;
                    }

                    yield break;
                }
            }
        }
        finally
        {
            Volatile.Write(ref _reading, 0);
        }
    }
}
