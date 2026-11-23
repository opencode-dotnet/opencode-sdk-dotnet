using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>Reads session costs including every descendant subagent session.</summary>
public static class SessionsClientExtensions
{
    /// <summary>Returns a session's own cost plus the cost of every descendant session, or only its own cost for a child session.</summary>
    /// <param name="client">The sessions family.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="requestOptions">Options for each request.</param>
    /// <param name="cancellationToken">Cancels the session read or children traversal.</param>
    /// <returns>The session's cost total.</returns>
    /// <remarks>
    /// Reads the session, then every descendant session depth-first, each level across every page,
    /// and counts each session once. NoThrow is refused at the call. Every failure propagates,
    /// including the server's not-found error for an unknown session.
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
        var session = client.GetSessionClient(sessionId);
        return new SessionCostTotal(client).ReadAsync(session, sessionId, requestOptions, cancellationToken);
    }
}
