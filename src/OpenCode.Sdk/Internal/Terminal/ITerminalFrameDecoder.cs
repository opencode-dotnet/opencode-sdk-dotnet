using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Reads one assembled terminal WebSocket message as the frame it carries. This isolates the
/// family fact <see cref="TerminalSocketCore{TFrame}"/> must not know: what a message means — a
/// text chunk, a marker-prefixed control body, a family-specific envelope — belongs to the family,
/// while receiving and reassembling it belongs to the socket.
/// </summary>
/// <typeparam name="TFrame">The frame type the family's read enumeration yields.</typeparam>
internal interface ITerminalFrameDecoder<out TFrame>
    where TFrame : class
{
    /// <summary>Reads an assembled message as the frame it carries.</summary>
    /// <param name="messageType">The message type the socket reported.</param>
    /// <param name="payload">The buffer the assembled message lives in.</param>
    /// <param name="count">How many bytes of the buffer the message occupies.</param>
    /// <returns>The frame the message carries.</returns>
    public TFrame Decode(WebSocketMessageType messageType, byte[] payload, int count);
}
