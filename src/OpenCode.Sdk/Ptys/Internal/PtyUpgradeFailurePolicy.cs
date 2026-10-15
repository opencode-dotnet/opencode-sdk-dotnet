using System.Globalization;
using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Names why a PTY WebSocket upgrade never completed. The server answers a missing PTY, or a
/// requested location whose directory does not exist, with a plain HTTP 404 and a refused
/// credential or origin with 401/403 <em>before</em> upgrading, so there is no response spine for
/// the failure to ride and no envelope for the typed-error machinery to materialize: the transport
/// plane is the honest channel. On targets whose
/// <see cref="ClientWebSocket"/> cannot report the response status the failure still names the
/// connect context it does know.
/// </summary>
internal sealed class PtyUpgradeFailurePolicy : ITerminalUpgradeFailurePolicy
{
    private PtyUpgradeFailurePolicy()
    {
    }

    /// <summary>Gets the shared policy instance.</summary>
    public static PtyUpgradeFailurePolicy Instance { get; } = new();

    /// <inheritdoc />
    public OpenCodeTransportException Map(WebSocketException exception, int? status, string terminalId)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (status is null)
        {
            return new OpenCodeTransportException(
                $"The opencode PTY '{terminalId}' WebSocket upgrade failed before the connection was established.",
                exception);
        }

        var code = status.Value.ToString(CultureInfo.InvariantCulture);
        return status switch
        {
            404 => new OpenCodeTransportException(
                $"The opencode server answered the PTY '{terminalId}' WebSocket upgrade with HTTP {code}; the PTY session or the requested location does not exist.",
                exception),
            401 or 403 => new OpenCodeTransportException(
                $"The opencode server refused the PTY '{terminalId}' WebSocket upgrade with HTTP {code}; the request's credential or origin was rejected.",
                exception),
            _ => new OpenCodeTransportException(
                $"The opencode server answered the PTY '{terminalId}' WebSocket upgrade with HTTP {code} instead of completing the protocol upgrade.",
                exception),
        };
    }
}
