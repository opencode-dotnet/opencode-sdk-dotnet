using System.Buffers.Binary;
using System.Text;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The persistent-PTY wire literals the session tests run on: the JSON control frames exactly as
/// the handler emits them and the framed-input layout the server parses. Knowledge source:
/// upstream-observed at 106629aa (handler:138-182, 212-219).
/// </summary>
internal static class PersistentPtyFrameData
{
    /// <summary>
    /// The info literal up to its <c>size</c> member. Every member but the viewport is the same
    /// terminal in each variant below, so the size object is the only part a variant restates.
    /// </summary>
    private const string InfoHead =
        "{\"id\":\"pty_persistent_7\",\"title\":\"sdk terminal\",\"userTitle\":null,\"command\":\"/bin/bash\",\"args\":[\"-l\"],\"cwd\":\"/\",\"status\":\"running\",\"pid\":5150,\"sessionID\":\"ses_1\",\"foregroundProcess\":null,\"size\":";

    /// <summary>The info literal after its <c>size</c> member.</summary>
    private const string InfoTail = ",\"output\":{\"head\":0,\"tail\":42}}";

    /// <summary>The terminal at the server's own default viewport.</summary>
    public const string InfoJson = InfoHead + "{\"cols\":80,\"rows\":24}" + InfoTail;

    /// <summary>
    /// The same terminal at a viewport the server did not default to. Only the size differs, so a
    /// framed write proves it carries the size the server reported rather than a built-in 80x24.
    /// </summary>
    public const string WideInfoJson = InfoHead + "{\"cols\":132,\"rows\":50}" + InfoTail;

    public const string AttachedJson =
        "{\"type\":\"attached\",\"attachmentID\":\"att_1\",\"inputProtocol\":1,\"info\":" + InfoJson +
        ",\"role\":\"controller\",\"generation\":3,\"replay\":{\"requestedOffset\":0,\"availableOffset\":0,\"endOffset\":42,\"truncated\":false}}";

    public const string AttachedObserverJson =
        "{\"type\":\"attached\",\"attachmentID\":\"att_2\",\"inputProtocol\":1,\"info\":" + InfoJson +
        ",\"role\":\"observer\",\"generation\":3,\"replay\":{\"requestedOffset\":10,\"availableOffset\":20,\"endOffset\":42,\"truncated\":true}}";

    public const string AttachedRawProtocolJson =
        "{\"type\":\"attached\",\"attachmentID\":\"att_3\",\"inputProtocol\":0,\"info\":" + InfoJson +
        ",\"role\":\"controller\",\"generation\":3,\"replay\":{\"requestedOffset\":0,\"availableOffset\":0,\"endOffset\":0,\"truncated\":false}}";

    /// <summary>An attach reporting a terminal the server did not create at its default viewport.</summary>
    public const string AttachedWideViewportJson =
        "{\"type\":\"attached\",\"attachmentID\":\"att_6\",\"inputProtocol\":1,\"info\":" + WideInfoJson +
        ",\"role\":\"controller\",\"generation\":3,\"replay\":{\"requestedOffset\":0,\"availableOffset\":0,\"endOffset\":42,\"truncated\":false}}";

    /// <summary>An attach whose promised <c>attachmentID</c> string the server sent as JSON null.</summary>
    public const string AttachedNullAttachmentIdJson =
        "{\"type\":\"attached\",\"attachmentID\":null,\"inputProtocol\":1,\"info\":" + InfoJson +
        ",\"role\":\"controller\",\"generation\":3,\"replay\":{\"requestedOffset\":0,\"availableOffset\":0,\"endOffset\":42,\"truncated\":false}}";

    /// <summary>An attach whose promised <c>role</c> string the server sent as JSON null.</summary>
    public const string AttachedNullRoleJson =
        "{\"type\":\"attached\",\"attachmentID\":\"att_4\",\"inputProtocol\":1,\"info\":" + InfoJson +
        ",\"role\":null,\"generation\":3,\"replay\":{\"requestedOffset\":0,\"availableOffset\":0,\"endOffset\":42,\"truncated\":false}}";

    /// <summary>An attach whose <c>role</c> is a spelling the wire's two literals do not carry.</summary>
    public const string AttachedUnknownRoleJson =
        "{\"type\":\"attached\",\"attachmentID\":\"att_5\",\"inputProtocol\":1,\"info\":" + InfoJson +
        ",\"role\":\"supervisor\",\"generation\":3,\"replay\":{\"requestedOffset\":0,\"availableOffset\":0,\"endOffset\":42,\"truncated\":false}}";

    public const string ReplayCompleteJson = "{\"type\":\"replay_complete\",\"endOffset\":42}";

    /// <summary>The checkpoint is base64 of ESC c (0x1B 0x63).</summary>
    public const string ResizedJson = "{\"type\":\"resized\",\"cols\":120,\"rows\":40,\"generation\":4,\"checkpoint\":\"G2M=\"}";

    /// <summary>A resize whose type is readable but whose <c>cols</c> member the server left out.</summary>
    public const string ResizedWithoutColsJson = "{\"type\":\"resized\",\"rows\":40,\"generation\":4,\"checkpoint\":\"G2M=\"}";

    public const string ExitedJson = "{\"type\":\"exited\",\"exitCode\":0,\"finalOffset\":99}";

    public const string ExitedWithoutCodeJson = "{\"type\":\"exited\",\"finalOffset\":99}";

    public const string ControllerChangedJson = "{\"type\":\"controller_changed\",\"attachmentID\":\"att_9\",\"generation\":5}";

    /// <summary>
    /// A controller change leaving the terminal with no controller at all: upstream omits the
    /// <c>attachmentID</c> member entirely in that state rather than sending it as null
    /// (daemon.ts:552).
    /// </summary>
    public const string ControllerChangedWithoutAttachmentIdJson = "{\"type\":\"controller_changed\",\"generation\":6}";

    public const string TitleChangedJson = "{\"type\":\"title_changed\",\"title\":\"vim\"}";

    /// <summary>A title change whose promised <c>title</c> string the server sent as JSON null.</summary>
    public const string TitleChangedNullTitleJson = "{\"type\":\"title_changed\",\"title\":null}";

    public const string ForegroundProcessChangedJson = "{\"type\":\"foreground_process_changed\",\"process\":null}";

    /// <summary>A foreground-process change naming the process that took the terminal.</summary>
    public const string ForegroundProcessChangedWithProcessJson = "{\"type\":\"foreground_process_changed\",\"process\":\"vim\"}";

    public const string UnknownTypeJson = "{\"type\":\"scrollback_trimmed\",\"bytes\":1024}";

    public const string TypelessJson = "{\"cols\":1}";

    public const string TruncatedJson = "{\"type\":\"resized\",";

    public const string TerminalUnavailableReason = "terminal unavailable";

    public static byte[] Output(string text) => Encoding.UTF8.GetBytes(text);

    /// <summary>Builds the framed input the server parses: [type u8][cols u16 BE][rows u16 BE][data].</summary>
    public static byte[] Framed(byte type, int cols, int rows, byte[] data)
    {
        ArgumentNullException.ThrowIfNull(data);

        var frame = new byte[5 + data.Length];
        frame[0] = type;
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(1, 2), checked((ushort)cols));
        BinaryPrimitives.WriteUInt16BigEndian(frame.AsSpan(3, 2), checked((ushort)rows));
        data.CopyTo(frame, 5);
        return frame;
    }
}
