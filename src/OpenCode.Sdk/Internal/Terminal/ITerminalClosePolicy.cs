using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Reads a terminal WebSocket close frame as an ending or a failure. This isolates the family fact
/// <see cref="TerminalSocketCore{TFrame}"/> must not know: which statuses end a read normally, and
/// which application close code names which failure, is a per-family protocol decision.
/// </summary>
internal interface ITerminalClosePolicy
{
    /// <summary>Maps a close frame onto the failure it carries, or null when the close ends the read normally.</summary>
    /// <param name="status">The status the peer closed with.</param>
    /// <param name="description">The reason the peer closed with, when it sent one.</param>
    /// <returns>The transport failure to throw, or null for a normal end.</returns>
    public OpenCodeTransportException? Map(WebSocketCloseStatus? status, string? description);
}
