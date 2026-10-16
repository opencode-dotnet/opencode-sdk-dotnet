using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>Owns one fixed send budget and the caller/connection/timeout failure precedence.</summary>
internal sealed class TerminalSendDeadline : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationToken _caller;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _operation;
    private readonly CancellationTokenRegistration _interruption;
    private readonly Func<TerminalConnectionOutcome?> _getOutcome;
    private readonly Type _owner;
    private Task? _physicalSend;
    private bool _sendThrew;
    private bool _timeoutAccepted;

    public TerminalSendDeadline(
        Type owner,
        TimeSpan timeout,
        ITerminalDeadlineFactory factory,
        Func<TerminalConnectionOutcome?> getOutcome,
        CancellationToken caller)
    {
        _owner = owner;
        _caller = caller;
        _getOutcome = getOutcome;
        _deadline = factory.Create(timeout);
        _operation = CancellationTokenSource.CreateLinkedTokenSource(caller, _deadline.Token);
        // Cancellation callbacks run in LIFO order: latch before the link interrupts physical I/O.
        // A deadline already canceled during construction has no admitted operation to interrupt.
        _interruption = _deadline.Token.Register(Interrupt);
    }

    /// <summary>Gets the combined physical-send cancellation token.</summary>
    public CancellationToken Token => _operation.Token;

    /// <summary>Captures the physical operation at socket entry, before any failure mapping.</summary>
    public Task SendAsync(ITerminalWebSocket socket, ArraySegment<byte> payload, WebSocketMessageType messageType)
    {
        lock (_gate)
        {
            try
            {
                return _physicalSend = socket.SendAsync(payload, messageType, Token);
            }
            catch
            {
                _sendThrew = true;
                throw;
            }
        }
    }

    /// <summary>Classifies a failed send without replacing an earlier connection outcome.</summary>
    public Exception Map(Exception exception, bool queued, bool disposed, TerminalConnectionOutcome? outcome)
    {
        if (_caller.IsCancellationRequested)
        {
            return new OperationCanceledException("The opencode PTY WebSocket send was canceled.", exception, _caller);
        }

        if (queued && disposed)
        {
            return new ObjectDisposedException(_owner.FullName);
        }

        lock (_gate)
        {
            if (_timeoutAccepted)
            {
                var message = queued
                    ? "The opencode PTY WebSocket send timed out while waiting for send admission."
                    : "The opencode PTY WebSocket send timed out during the socket write; delivery is uncertain.";
                return new OpenCodeTransportException(message, new TimeoutException(message, exception));
            }
        }

        return outcome?.SendFailure ?? FailureClassification.Map(exception, FailurePhase.PtyWebSocketWrite, _caller);
    }

    /// <summary>Releases the operation's registrations and timer once its send settles.</summary>
    public void Dispose()
    {
        _interruption.Dispose();
        _operation.Dispose();
        _deadline.Dispose();
    }

    private void Interrupt()
    {
        lock (_gate)
        {
            // Task state is already final even when its asynchronous continuations have not run.
            _timeoutAccepted = !_sendThrew && _physicalSend?.IsCompleted is not true && _getOutcome() is null;
        }
    }
}
