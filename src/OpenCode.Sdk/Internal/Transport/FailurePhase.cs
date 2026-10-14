namespace OpenCode.Sdk.Internal;

/// <summary>The pipeline phase a failure is classified for; the phase names the thrown messages.</summary>
internal enum FailurePhase
{
    /// <summary>Sending the request and awaiting response headers.</summary>
    Send,

    /// <summary>Reading and decoding a buffered response body.</summary>
    ResponseBodyRead,

    /// <summary>Opening or reading a live event-stream body.</summary>
    EventStreamRead,

    /// <summary>Receiving a message on a live PTY WebSocket.</summary>
    PtyWebSocketRead,

    /// <summary>Sending a message on, or closing, a live PTY WebSocket.</summary>
    PtyWebSocketWrite,
}
