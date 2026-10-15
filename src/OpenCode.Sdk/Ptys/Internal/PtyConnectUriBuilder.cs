using System.Globalization;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Composes the address of a PTY WebSocket upgrade. The operation is transport-owned, so no
/// generated route builder exists for it (ADR-0021): the path is spelled here, while the query
/// rides the same <see cref="QueryStringBuilder"/> every generated route uses, so the location
/// is encoded exactly as it is everywhere else.
/// </summary>
internal static class PtyConnectUriBuilder
{
    private const string ConnectRouteSuffix = "/connect";

    private const string CursorParameterName = "cursor";

    private const string LocationParameterName = "location";

    private const string PtyRoutePrefix = "/api/pty/";

    /// <summary>Builds the upgrade address for one PTY connect call.</summary>
    /// <param name="connection">The construction-time connection facts.</param>
    /// <param name="ptyId">The PTY the upgrade addresses.</param>
    /// <param name="options">The per-call connect options, when the caller supplied any.</param>
    /// <returns>The absolute <c>ws</c> or <c>wss</c> address to upgrade.</returns>
    public static Uri Build(ConnectionSnapshot connection, string ptyId, PtyConnectOptions? options)
    {
        ArgumentNullException.ThrowIfNull(connection);
        ArgumentException.ThrowIfNullOrWhiteSpace(ptyId);

        // The connect scope must resolve exactly as the token door's scope resolves, so the
        // merge is the same member-by-member rule the header channel applies.
        var query = new QueryStringBuilder();
        query.AddLocation(LocationParameterName, LocationMerge.Resolve(options?.Location, connection.Location));
        query.AddText(CursorParameterName, options?.Cursor?.ToString(CultureInfo.InvariantCulture));

        return new Uri(
            string.Concat(
                WebSocketSchemePolicy.ToWebSocketScheme(connection.EndpointBase),
                PtyRoutePrefix,
                RouteValuePolicy.EscapeSegment(ptyId, nameof(ptyId)),
                ConnectRouteSuffix,
                query.Value),
            UriKind.Absolute);
    }
}
