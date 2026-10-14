namespace OpenCode.Sdk.Internal;

/// <summary>The immutable first connection outcome shared by readers and writers.</summary>
internal sealed record TerminalConnectionOutcome(Exception? Failure)
{
    /// <summary>Gets the failure a send observes after this connection ended.</summary>
    public Exception SendFailure => Failure ?? new OpenCodeTransportException("The opencode PTY WebSocket connection has ended.");
}
