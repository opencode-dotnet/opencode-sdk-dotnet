using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>Reads session costs including their direct subagent sessions.</summary>
public static class SessionsClientExtensions
{
    /// <summary>Returns a session's own cost plus its direct children's costs, or only its own cost for a child session.</summary>
    /// <param name="client">The sessions family.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="requestOptions">Options for each request.</param>
    /// <param name="cancellationToken">Cancels the session read or children traversal.</param>
    /// <returns>The session's cost total.</returns>
    /// <remarks>
    /// Reads the session, then its direct children across every page for a root session.
    /// Grandchildren, which exist only when the server's <c>experimental.subagent_depth</c> is above
    /// 1, are not included. NoThrow is refused at the call. Every failure propagates, including the
    /// server's not-found error for an unknown session.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> is null.</exception>
    /// <exception cref="ArgumentException">The session identifier is invalid, or <paramref name="requestOptions"/> selects NoThrow.</exception>
    /// <exception cref="OpenCodeApiException">The session read or children traversal returned an API error.</exception>
    /// <exception cref="OpenCodeTransportException">The session read or children traversal failed in transport.</exception>
    /// <exception cref="OperationCanceledException">The caller canceled the session read or children traversal.</exception>
    public static Task<double> GetTotalCostAsync(this SessionsClient client, string sessionId,
        OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ErrorBehaviorPolicy.RefuseNoThrow(requestOptions, "Session cost totals");
        return ReadTotalCostAsync(client, sessionId, requestOptions, cancellationToken);
    }

    private static async Task<double> ReadTotalCostAsync(SessionsClient client, string sessionId,
        OpenCodeRequestOptions? requestOptions, CancellationToken cancellationToken)
    {
        var response = await client.GetSessionClient(sessionId).GetAsync(requestOptions, cancellationToken).ConfigureAwait(false);
        var session = response.Session;
        if (session.ParentId is not null)
        {
            return session.Cost;
        }

        var total = session.Cost;
        await foreach (var child in client.EnumerateSessionsAsync(new SessionListRequest
        {
            ParentId = SessionParentFilter.Of(sessionId),
            Order = ListOrder.Descending,
            Limit = 200,
        }, requestOptions, cancellationToken).ConfigureAwait(false))
        {
            total += child.Cost;
        }

        return total;
    }
}
