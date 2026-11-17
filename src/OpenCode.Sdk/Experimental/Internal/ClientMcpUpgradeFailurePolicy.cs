using System.Globalization;
using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>Retains the actual upgrade status, or the downlevel unknown-status context.</summary>
internal sealed class ClientMcpUpgradeFailurePolicy : ITerminalUpgradeFailurePolicy
{
    public OpenCodeTransportException Map(WebSocketException exception, int? status, string terminalId) =>
        new($"The opencode client MCP '{terminalId}' WebSocket upgrade failed" +
            (status is { } code ? $" with HTTP {code.ToString(CultureInfo.InvariantCulture)}." : " before the connection was established."), exception);
}
