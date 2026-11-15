namespace OpenCode.Sdk.Internal;

/// <summary>The immutable first connection outcome shared by readers and writers.</summary>
internal sealed record TerminalConnectionOutcome(Exception? Failure, WebSocketFailureContext? Context = null)
{
    /// <summary>Gets the failure a send observes after this connection ended.</summary>
    public Exception SendFailure => Failure ?? new OpenCodeTransportException($"{(Context ?? WebSocketFailureContext.Terminal).Prefix} connection has ended.");
}
