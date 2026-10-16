using System.Text;

namespace OpenCode.Sdk.Tests.Ptys.Support;

/// <summary>
/// The PTY wire literals the session tests run on. The control frame's exact byte shape — a
/// <c>0x00</c> marker followed by UTF-8 JSON — is the subject under test, so it lives here once
/// instead of being retyped per test method.
/// </summary>
internal static class PtyFrameData
{
    /// <summary>The single byte that marks a binary message as the cursor control frame.</summary>
    public const byte ControlFrameMarker = 0x00;

    /// <summary>The cursor value <see cref="CursorControlJson"/> carries.</summary>
    public const long CursorValue = 8_675_309;

    /// <summary>A well-formed control-frame body.</summary>
    public const string CursorControlJson = "{\"cursor\":8675309}";

    /// <summary>A control-frame body cut off mid-object.</summary>
    public const string TruncatedControlJson = "{\"cursor\":";

    /// <summary>A control-frame body that is valid JSON but carries no cursor.</summary>
    public const string CursorlessControlJson = "{\"offset\":7}";

    /// <summary>A control-frame body whose cursor is not a number.</summary>
    public const string NonNumericCursorControlJson = "{\"cursor\":\"7\"}";

    /// <summary>
    /// The WTF-8 encoding of an unpaired high surrogate (U+D83D). A replay chunk boundary can
    /// split a surrogate pair, so the SDK must decode this with replacement rather than fault.
    /// </summary>
    public static byte[] UnpairedSurrogate => [0xED, 0xA0, 0xBD];

    /// <summary>Builds the binary control frame carrying the given JSON body.</summary>
    public static byte[] ControlFrame(string json)
    {
        ArgumentNullException.ThrowIfNull(json);

        return [ControlFrameMarker, .. Encoding.UTF8.GetBytes(json)];
    }
}
