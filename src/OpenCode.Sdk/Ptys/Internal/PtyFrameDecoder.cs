using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The normal PTY family's frame decoder: the seam form of <see cref="PtyFrameReader"/>, holding
/// no state of its own, so one shared instance serves every session. Knowledge source:
/// upstream-observed — this family chunks its replay at 64Ki UTF-16 code units, so one message
/// can reach roughly 192 KiB of UTF-8 and arrives across many
/// <see cref="TerminalSocketBounds.ReceiveBufferSize"/> receives.
/// </summary>
internal sealed class PtyFrameDecoder : ITerminalFrameDecoder<PtyFrame>
{
    private PtyFrameDecoder()
    {
    }

    /// <summary>Gets the shared decoder instance.</summary>
    public static PtyFrameDecoder Instance { get; } = new();

    /// <inheritdoc />
    public PtyFrame Decode(WebSocketMessageType messageType, byte[] payload, int count) =>
        PtyFrameReader.Read(messageType, payload, count);
}
