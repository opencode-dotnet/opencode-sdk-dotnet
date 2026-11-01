using System.Net.WebSockets;
using System.Text;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Performance.Tests.Support;

/// <summary>
/// The <c>attached</c> grant a persistent-PTY session is built from: one wire frame, and the
/// attachment decoded from it by the SDK's own decoder, so a benchmark's session can never hold a
/// grant the decoder would not produce.
/// </summary>
internal sealed class PersistentPtySeed
{
    /// <summary>The attachment id the frame grants.</summary>
    public const string AttachmentId = "att_bench";

    private const string InfoJson =
        "{\"id\":\"pty_bench\",\"title\":\"bench\",\"userTitle\":null,\"command\":\"/bin/bash\",\"args\":[],\"cwd\":\"/\",\"status\":\"running\","
        + "\"pid\":1,\"sessionID\":\"ses_bench\",\"foregroundProcess\":null,\"size\":{\"cols\":80,\"rows\":24},"
        + "\"output\":{\"head\":0,\"tail\":0}}";

    private const string AttachedFrameJson =
        "{\"type\":\"attached\",\"attachmentID\":\"" + AttachmentId + "\",\"inputProtocol\":1,\"info\":" + InfoJson
        + ",\"role\":\"controller\",\"generation\":1,\"replay\":{\"requestedOffset\":0,\"availableOffset\":0,\"endOffset\":0,\"truncated\":false}}";

    /// <summary>Gets the <c>attached</c> frame as the server sends it, one text message.</summary>
    public byte[] AttachedFrame { get; } = Encoding.UTF8.GetBytes(AttachedFrameJson);

    /// <summary>Decodes <see cref="AttachedFrame"/> into the grant a session's constructor requires.</summary>
    /// <returns>The attachment.</returns>
    public PersistentPtyAttachment Attachment() =>
        ((PersistentPtyAttachedFrame)PersistentPtyFrameDecoder.Instance.Decode(WebSocketMessageType.Text, AttachedFrame, AttachedFrame.Length))
        .Attachment;
}
