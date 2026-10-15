using System.Globalization;
using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Reads a PTY WebSocket close frame as an ending or a failure. Knowledge source:
/// upstream-observed — the server closes with 1000 when the pseudo-terminal's process ends (the
/// exit code is not on the wire; a reader asks <c>GetAsync</c> for it) and with the
/// application code 4404 when the session is gone. An already-exited PTY still upgrades cleanly,
/// so 4404 surfaces on the first read rather than on connect.
/// </summary>
internal static class PtyCloseFailurePolicy
{
    private const WebSocketCloseStatus SessionNotFound = (WebSocketCloseStatus)4404;

    /// <summary>Maps a close frame onto the failure it carries, or null when the close ends the read normally.</summary>
    /// <param name="status">The status the peer closed with.</param>
    /// <param name="description">The reason the peer closed with, when it sent one.</param>
    /// <returns>The transport failure to throw, or null for a normal end.</returns>
    public static OpenCodeTransportException? Map(WebSocketCloseStatus? status, string? description)
    {
        if (status is WebSocketCloseStatus.NormalClosure)
        {
            return null;
        }

        if (status is SessionNotFound)
        {
            return new OpenCodeTransportException(
                $"The opencode server closed the PTY WebSocket with status 4404{FormatReason(description)}; the PTY session was not found or had already exited.");
        }

        var code = status is null
            ? "no status"
            : ((int)status.Value).ToString(CultureInfo.InvariantCulture);
        return new OpenCodeTransportException(
            $"The opencode PTY WebSocket closed abnormally with status {code}{FormatReason(description)}.");
    }

    private static string FormatReason(string? description) =>
        string.IsNullOrEmpty(description) ? string.Empty : $" ({description})";
}
