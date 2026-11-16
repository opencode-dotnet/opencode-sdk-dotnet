using System.Globalization;
using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>Retains actual peer close details; no close code is a permanent-stop or PTY verdict.</summary>
internal sealed class ClientMcpClosePolicy(string name) : ITerminalClosePolicy
{
    public OpenCodeTransportException Map(WebSocketCloseStatus? status, string? description) =>
        new($"The opencode client MCP '{name}' WebSocket closed with code {((int?)status)?.ToString(CultureInfo.InvariantCulture) ?? "unknown"} ({description}).");
}
