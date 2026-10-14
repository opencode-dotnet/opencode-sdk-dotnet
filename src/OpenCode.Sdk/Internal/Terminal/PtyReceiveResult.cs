using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// One PTY WebSocket receive, reduced to the three facts the session reads. It exists because the
/// modern receive overload answers a struct without close details while the downlevel one answers
/// a class with them; the seam reports the same shape on every target and exposes the close status
/// on the socket itself.
/// </summary>
/// <param name="MessageType">Whether the bytes are text, binary, or a close frame.</param>
/// <param name="Count">How many bytes were written into the caller's buffer.</param>
/// <param name="EndOfMessage">Whether this receive completed the message it belongs to.</param>
internal readonly record struct PtyReceiveResult(WebSocketMessageType MessageType, int Count, bool EndOfMessage);
