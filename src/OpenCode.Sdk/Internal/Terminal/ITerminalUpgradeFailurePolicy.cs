using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Names why a terminal WebSocket upgrade never completed. This isolates the family fact
/// <see cref="ClientTerminalWebSocket"/> must not know: a refused upgrade has no response spine, so the
/// wording that explains a status belongs to the family whose door was refused, not to the shared
/// adapter that performed the upgrade.
/// </summary>
internal interface ITerminalUpgradeFailurePolicy
{
    /// <summary>Maps a failed upgrade onto the transport failure that explains it.</summary>
    /// <param name="exception">The failure <see cref="ClientWebSocket.ConnectAsync(Uri, CancellationToken)"/> raised.</param>
    /// <param name="status">The HTTP status the server answered, or null when the target cannot report one.</param>
    /// <param name="terminalId">The terminal the upgrade addressed.</param>
    /// <returns>The transport failure to throw.</returns>
    public OpenCodeTransportException Map(WebSocketException exception, int? status, string terminalId);
}
