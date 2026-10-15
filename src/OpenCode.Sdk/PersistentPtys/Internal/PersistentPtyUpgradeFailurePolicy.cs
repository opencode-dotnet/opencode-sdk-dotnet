using System.Globalization;
using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Names why a persistent PTY WebSocket upgrade never completed. Knowledge source:
/// upstream-observed — this family checks the connect query and the credential before upgrading
/// but deliberately does not check that the terminal exists, so there is no 404 arm here: a
/// missing terminal or an absent daemon upgrades and then closes 4404, which
/// <see cref="PersistentPtyClosePolicy"/> owns. A refused upgrade has no response spine for
/// ADR-0007's machinery to materialize, so the transport plane is the honest channel.
/// </summary>
internal sealed class PersistentPtyUpgradeFailurePolicy : ITerminalUpgradeFailurePolicy
{
    private PersistentPtyUpgradeFailurePolicy()
    {
    }

    /// <summary>Gets the shared policy instance.</summary>
    public static PersistentPtyUpgradeFailurePolicy Instance { get; } = new();

    /// <inheritdoc />
    public OpenCodeTransportException Map(WebSocketException exception, int? status, string terminalId)
    {
        ArgumentNullException.ThrowIfNull(exception);

        if (status is null)
        {
            return new OpenCodeTransportException(
                $"The opencode persistent PTY '{terminalId}' WebSocket upgrade failed before the connection was established.",
                exception);
        }

        var code = status.Value.ToString(CultureInfo.InvariantCulture);
        return status switch
        {
            400 => new OpenCodeTransportException(
                $"The opencode server answered the persistent PTY '{terminalId}' WebSocket upgrade with HTTP {code}; the connect query was rejected (the cursor must be a safe integer at or above zero).",
                exception),
            401 or 403 => new OpenCodeTransportException(
                $"The opencode server refused the persistent PTY '{terminalId}' WebSocket upgrade with HTTP {code}; the request's credential or origin was rejected.",
                exception),
            _ => new OpenCodeTransportException(
                $"The opencode server answered the persistent PTY '{terminalId}' WebSocket upgrade with HTTP {code} instead of completing the protocol upgrade.",
                exception),
        };
    }
}
