using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The live terminal WebSocket a <see cref="TerminalSocketCore{TFrame}"/> reads, writes, and
/// closes. It is the seam a session test scripts: connecting is the door's job, so it is
/// deliberately absent here — what a session consumes is exactly this.
/// </summary>
internal interface ITerminalWebSocket : IDisposable
{
    /// <summary>Gets the status the peer closed with, once a close frame arrived.</summary>
    public WebSocketCloseStatus? CloseStatus { get; }

    /// <summary>Gets the reason the peer closed with, once a close frame arrived.</summary>
    public string? CloseStatusDescription { get; }

    /// <summary>Receives the next message fragment into the buffer.</summary>
    /// <param name="buffer">The buffer the fragment is written into.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>What the receive delivered.</returns>
    public Task<PtyReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken);

    /// <summary>Sends one complete message of the given type; the caller serializes sends.</summary>
    /// <param name="buffer">The bytes to send.</param>
    /// <param name="messageType">The message type to send them as.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>A task that completes once the message is sent.</returns>
    public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, CancellationToken cancellationToken);

    /// <summary>Closes the output half with the normal-closure status.</summary>
    /// <param name="cancellationToken">The cancellation token bounding the close.</param>
    /// <returns>A task that completes once the close frame is sent.</returns>
    public Task CloseOutputAsync(CancellationToken cancellationToken);
}
