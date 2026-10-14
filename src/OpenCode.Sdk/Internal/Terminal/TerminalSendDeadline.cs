namespace OpenCode.Sdk.Internal;

/// <summary>Owns one fixed send budget and the caller/connection/timeout failure precedence.</summary>
internal sealed class TerminalSendDeadline : IDisposable
{
    private readonly CancellationToken _caller;
    private readonly CancellationTokenSource _deadline;
    private readonly CancellationTokenSource _operation;
    private readonly Type _owner;

    public TerminalSendDeadline(Type owner, TimeSpan timeout, ITerminalDeadlineFactory factory, CancellationToken caller)
    {
        _owner = owner;
        _caller = caller;
        _deadline = factory.Create(timeout);
        _operation = CancellationTokenSource.CreateLinkedTokenSource(caller, _deadline.Token);
    }

    /// <summary>Gets the combined physical-send cancellation token.</summary>
    public CancellationToken Token => _operation.Token;

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

        if (outcome is not null)
        {
            return outcome.SendFailure;
        }

        if (_deadline.IsCancellationRequested)
        {
            var message = queued
                ? "The opencode PTY WebSocket send timed out while waiting for send admission."
                : "The opencode PTY WebSocket send timed out during the socket write; delivery is uncertain.";
            return new OpenCodeTransportException(message, new TimeoutException(message, exception));
        }

        return FailureClassification.Map(exception, FailurePhase.PtyWebSocketWrite, _caller);
    }

    /// <summary>Releases the operation's registrations and timer once its send settles.</summary>
    public void Dispose()
    {
        _operation.Dispose();
        _deadline.Dispose();
    }
}
