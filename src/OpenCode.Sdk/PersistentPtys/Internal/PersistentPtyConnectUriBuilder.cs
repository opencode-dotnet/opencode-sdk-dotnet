using System.Globalization;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Composes the address of a persistent PTY WebSocket upgrade. The operation is transport-owned,
/// so no generated route builder exists for it (ADR-0021): the path is spelled here, while the
/// query rides the same <see cref="QueryStringBuilder"/> every generated route uses. Unlike the
/// normal family the query carries no location — this family's terminals are keyed by id alone —
/// and it always negotiates the framed input protocol, which is the only protocol this SDK writes.
/// </summary>
internal static class PersistentPtyConnectUriBuilder
{
    private const string AttachmentIdParameterName = "attachment_id";

    private const string ConnectRouteSuffix = "/connect";

    private const string CursorParameterName = "cursor";

    private const string FramedInputProtocol = "1";

    private const string InputProtocolParameterName = "input_protocol";

    private const string ObserverRole = "observer";

    private const string PersistentPtyRoutePrefix = "/api/experimental/persistent-pty/";

    private const string RoleParameterName = "role";

    private const string TakeoverParameterName = "takeover";

    /// <summary>Builds the upgrade address for one persistent PTY connect call.</summary>
    /// <param name="connection">The construction-time connection facts.</param>
    /// <param name="ptyId">The terminal the upgrade addresses.</param>
    /// <param name="options">The per-call connect options, when the caller supplied any.</param>
    /// <returns>The absolute <c>ws</c> or <c>wss</c> address to upgrade.</returns>
    public static Uri Build(ConnectionSnapshot connection, string ptyId, PersistentPtyConnectOptions? options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ptyId);

        var query = new QueryStringBuilder();
        query.AddText(CursorParameterName, options?.Cursor?.ToString(CultureInfo.InvariantCulture));
        query.AddText(RoleParameterName, options?.Role is PersistentPtyRole.Observer ? ObserverRole : null);
        query.AddText(AttachmentIdParameterName, options?.AttachmentId);
        query.AddText(TakeoverParameterName, options?.Takeover is true ? "true" : null);

        // Always negotiated: the session writes framed input only, and it refuses an attachment
        // the server answers with any other protocol.
        query.AddText(InputProtocolParameterName, FramedInputProtocol);

        return new Uri(
            string.Concat(
                WebSocketSchemePolicy.ToWebSocketScheme(connection.EndpointBase),
                PersistentPtyRoutePrefix,
                RouteValuePolicy.EscapeSegment(ptyId, nameof(ptyId)),
                ConnectRouteSuffix,
                query.Value),
            UriKind.Absolute);
    }
}
