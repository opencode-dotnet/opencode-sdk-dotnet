using System.Net.WebSockets;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// A terminal decoder that reads nothing: it hands back one frame decoded once, for every
/// message, so a receive-only rung drives a session's receive loop (receive, reassembly, queue,
/// delivery) with decoding taken out.
/// </summary>
/// <typeparam name="TFrame">The frame type the family's read enumeration yields.</typeparam>
internal sealed class NoOpFrameDecoder<TFrame> : ITerminalFrameDecoder<TFrame>
    where TFrame : class
{
    private readonly TFrame _frame;

    public NoOpFrameDecoder(TFrame frame)
    {
        ArgumentNullException.ThrowIfNull(frame);
        _frame = frame;
    }

    public TFrame Decode(WebSocketMessageType messageType, byte[] payload, int count) => _frame;
}
