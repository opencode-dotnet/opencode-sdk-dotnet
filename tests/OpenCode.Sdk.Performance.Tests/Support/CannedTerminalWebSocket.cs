using System.Net.WebSockets;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// Replays a fixed run of PTY WebSocket messages from memory, one receive at a time bounded by
/// the caller's buffer — exactly the constraint <see cref="PtySession"/> reads under — so a
/// message larger than one receive triggers the same cross-receive reassembly a live socket
/// would produce. The run ends with a normal closure, mirroring how a session's read loop ends
/// without a fault. No socket, no live server. The replay reads the caller's array and never
/// copies it, so constructing one inside a measured rung costs one small object. By default
/// every receive completes synchronously; a suspending socket completes each receive on the
/// thread pool, as a socket whose data has not arrived yet does, and
/// <see cref="SuspendedReceives"/> lets a setup prove it did.
/// </summary>
internal sealed class CannedTerminalWebSocket : ITerminalWebSocket
{
    private readonly (WebSocketMessageType Type, byte[] Payload)[] _messages;
    private readonly bool _suspend;
    private int _messageIndex;
    private int _offset;
    private bool _closed;

    public CannedTerminalWebSocket((WebSocketMessageType Type, byte[] Payload)[] messages, bool suspend = false)
    {
        ArgumentNullException.ThrowIfNull(messages);
        _messages = messages;
        _suspend = suspend;
    }

    public WebSocketCloseStatus? CloseStatus => _closed ? WebSocketCloseStatus.NormalClosure : null;

    public string? CloseStatusDescription => null;

    /// <summary>Gets how many receives suspended before they returned.</summary>
    public int SuspendedReceives { get; private set; }

    /// <summary>
    /// Delivers the next fragment of the current message, or a normal-closure close once every
    /// message has been delivered. A message longer than <paramref name="buffer"/> splits across
    /// as many receives as it takes, exactly as <see cref="PtySession"/>'s fixed 16 KiB buffer
    /// would force a live socket to.
    /// </summary>
    public Task<PtyReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken) =>
        _suspend ? SuspendThenReceiveAsync(buffer) : Task.FromResult(Receive(buffer));

    /// <summary>Not exercised by the read-path benchmarks; a harmless no-op rather than a refusal.</summary>
    public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, CancellationToken cancellationToken) =>
        Task.CompletedTask;

    /// <summary>Not exercised by the read-path benchmarks; a harmless no-op rather than a refusal.</summary>
    public Task CloseOutputAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    /// <summary>Rewinds the replay to its first message, so one instance can be read from repeatedly.</summary>
    public void Reset()
    {
        _messageIndex = 0;
        _offset = 0;
        _closed = false;
    }

    public void Dispose()
    {
    }

    private async Task<PtyReceiveResult> SuspendThenReceiveAsync(ArraySegment<byte> buffer)
    {
        await Task.Yield();
        SuspendedReceives++;
        return Receive(buffer);
    }

    private PtyReceiveResult Receive(ArraySegment<byte> buffer)
    {
        if (_messageIndex >= _messages.Length)
        {
            _closed = true;
            return new PtyReceiveResult(WebSocketMessageType.Close, 0, EndOfMessage: true);
        }

        var (type, payload) = _messages[_messageIndex];
        var count = Math.Min(payload.Length - _offset, buffer.Count);
        Array.Copy(payload, _offset, buffer.Array!, buffer.Offset, count);
        _offset += count;
        var endOfMessage = _offset >= payload.Length;
        if (endOfMessage)
        {
            _messageIndex++;
            _offset = 0;
        }

        return new PtyReceiveResult(type, count, endOfMessage);
    }
}
