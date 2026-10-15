using System.Net.WebSockets;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// A connection that stays open and swallows what is sent: every receive waits until the session
/// cancels it, as a quiet terminal's socket does, so a session built on it accepts writes for as
/// long as a benchmark runs, and every send completes at once after counting the message.
/// </summary>
/// <remarks>
/// A setup can ask for the next message's bytes with <see cref="CaptureNext"/>; only that one send
/// copies, so a measured send never pays for the check.
/// </remarks>
internal sealed class SendSinkTerminalWebSocket : ITerminalWebSocket
{
    private bool _captureNext;

    /// <summary>Gets how many messages were sent.</summary>
    public int SentMessages { get; private set; }

    /// <summary>Gets the type of the last message sent.</summary>
    public WebSocketMessageType LastMessageType { get; private set; }

    /// <summary>Gets a copy of the message sent after the last <see cref="CaptureNext"/>, or null.</summary>
    public byte[]? Captured { get; private set; }

    public WebSocketCloseStatus? CloseStatus => null;

    public string? CloseStatusDescription => null;

    /// <summary>Asks for a copy of the next message sent.</summary>
    public void CaptureNext()
    {
        Captured = null;
        _captureNext = true;
    }

    /// <summary>A receive no data ever arrives for: it ends, cancelled, when the session cancels it, and not before.</summary>
    public async Task<PtyReceiveResult> ReceiveAsync(ArraySegment<byte> buffer, CancellationToken cancellationToken)
    {
        var receive = new TaskCompletionSource<PtyReceiveResult>(TaskCreationOptions.RunContinuationsAsynchronously);
        using (cancellationToken.Register(() => receive.TrySetCanceled(cancellationToken)))
        {
            return await receive.Task.ConfigureAwait(false);
        }
    }

    public Task SendAsync(ArraySegment<byte> buffer, WebSocketMessageType messageType, CancellationToken cancellationToken)
    {
        SentMessages++;
        LastMessageType = messageType;
        if (_captureNext)
        {
            Captured = [.. buffer.AsSpan()];
            _captureNext = false;
        }

        return Task.CompletedTask;
    }

    public Task CloseOutputAsync(CancellationToken cancellationToken) => Task.CompletedTask;

    public void Dispose()
    {
    }
}
