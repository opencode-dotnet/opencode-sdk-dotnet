using System.Diagnostics;
using System.Net.WebSockets;
using System.Text;
using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Reads one assembled PTY WebSocket message as the frame it is. Knowledge source:
/// upstream-observed — terminal output rides text frames, and the single binary control frame is
/// a <c>0x00</c> marker byte followed by UTF-8 JSON carrying the replay cursor, sent once after
/// the retained buffer is replayed. A binary message that does not start with the marker is
/// ordinary output.
/// </summary>
internal static class PtyFrameReader
{
    private const byte ControlFrameMarker = 0x00;

    private const string ControlFrameFailure =
        "The opencode server sent a PTY control frame whose body is not a JSON object carrying an integer 'cursor'.";

    private const string CursorPropertyName = "cursor";

    /// <summary>Reads an assembled message as an output or cursor frame.</summary>
    /// <param name="messageType">The message type the socket reported.</param>
    /// <param name="payload">The buffer the assembled message lives in.</param>
    /// <param name="count">How many bytes of the buffer the message occupies.</param>
    /// <returns>The frame the message carries.</returns>
    /// <exception cref="OpenCodeTransportException">The control frame's body is not readable as a cursor.</exception>
    public static PtyFrame Read(WebSocketMessageType messageType, byte[] payload, int count)
    {
        Debug.Assert(count >= 0 && count <= payload.Length, "The reported count never exceeds the buffer.");

        if (messageType is WebSocketMessageType.Binary && count > 0 && payload[0] is ControlFrameMarker)
        {
            return new PtyCursorFrame(ReadCursor(new ReadOnlySpan<byte>(payload, 1, count - 1)));
        }

        // Replacement decoding is deliberate, not laxity: the server chunks its replay at 64Ki
        // UTF-16 code units, so a chunk boundary can split a surrogate pair and produce bytes no
        // strict decoder accepts. Terminal output must survive that, not fault on it.
        return new PtyOutputFrame(Encoding.UTF8.GetString(payload, 0, count));
    }

    private static long ReadCursor(ReadOnlySpan<byte> body)
    {
        try
        {
            var reader = new Utf8JsonReader(body);
            if (!reader.Read() || reader.TokenType is not JsonTokenType.StartObject)
            {
                throw new OpenCodeTransportException(ControlFrameFailure);
            }

            while (reader.Read() && reader.TokenType is JsonTokenType.PropertyName)
            {
                var isCursor = reader.ValueTextEquals(CursorPropertyName);
                if (!reader.Read())
                {
                    break;
                }

                if (!isCursor)
                {
                    reader.Skip();
                    continue;
                }

                return reader.TokenType is JsonTokenType.Number && reader.TryGetInt64(out var cursor)
                    ? cursor
                    : throw new OpenCodeTransportException(ControlFrameFailure);
            }

            throw new OpenCodeTransportException(ControlFrameFailure);
        }
        catch (JsonException exception)
        {
            throw new OpenCodeTransportException(ControlFrameFailure, exception);
        }
    }
}
