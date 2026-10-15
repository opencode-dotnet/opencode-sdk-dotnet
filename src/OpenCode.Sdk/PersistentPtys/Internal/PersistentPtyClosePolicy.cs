using System.Globalization;
using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Reads a persistent PTY WebSocket close frame as an ending or a failure. Knowledge source:
/// upstream-observed — the server closes with 1000 after the terminal exits and whenever the
/// daemon's stream ends, and with the application code 4404 when the terminal does not exist or
/// the opencode-pty daemon is unavailable. Because this family performs no pre-upgrade existence
/// check, 4404 is the arm a missing terminal takes, and it arrives before the <c>attached</c>
/// frame rather than mid-stream. The policy holds no state, so one instance serves every session.
/// </summary>
internal sealed class PersistentPtyClosePolicy : ITerminalClosePolicy
{
    private const WebSocketCloseStatus TerminalUnavailable = (WebSocketCloseStatus)4404;

    private PersistentPtyClosePolicy()
    {
    }

    /// <summary>Gets the shared policy instance.</summary>
    public static PersistentPtyClosePolicy Instance { get; } = new();

    /// <inheritdoc />
    public OpenCodeTransportException? Map(WebSocketCloseStatus? status, string? description)
    {
        if (status is WebSocketCloseStatus.NormalClosure)
        {
            return null;
        }

        if (status is TerminalUnavailable)
        {
            return new OpenCodeTransportException(
                $"The opencode server closed the persistent PTY WebSocket with status 4404{FormatReason(description)}; the terminal does not exist or the opencode-pty daemon is unavailable.");
        }

        var code = status is null
            ? "no status"
            : ((int)status.Value).ToString(CultureInfo.InvariantCulture);
        return new OpenCodeTransportException(
            $"The opencode persistent PTY WebSocket closed abnormally with status {code}{FormatReason(description)}.");
    }

    private static string FormatReason(string? description) =>
        string.IsNullOrEmpty(description) ? string.Empty : $" ({description})";
}
