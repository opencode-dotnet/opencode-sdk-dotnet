namespace OpenCode.Sdk.Internal;

/// <summary>Totals every descendant's cost for a root, or a child's own cost, matching packages/client/src/solid/data.ts:1582-1590.</summary>
/// <param name="client">The sessions family.</param>
internal sealed class SessionCostTotal(SessionsClient client)
{
    /// <summary>Reads the session's own cost, adding every descendant only for a root session.</summary>
    /// <param name="session">The session client.</param>
    /// <param name="sessionId">The session identifier.</param>
    /// <param name="requestOptions">Options for each request.</param>
    /// <param name="cancellationToken">Cancels the read or traversal.</param>
    /// <returns>The session's cost total.</returns>
    public async Task<double> ReadAsync(SessionClient session, string sessionId,
        OpenCodeRequestOptions? requestOptions, CancellationToken cancellationToken)
    {
        var response = await session.GetAsync(requestOptions, cancellationToken).ConfigureAwait(false);
        var info = response.Session;
        if (info.ParentId is not null)
        {
            return info.Cost;
        }

        var visited = new HashSet<string>(StringComparer.Ordinal) { info.Id };
        return await ReadDescendantsAsync(sessionId, info.Cost, visited, requestOptions, cancellationToken).ConfigureAwait(false);
    }

    private async Task<double> ReadDescendantsAsync(string sessionId, double total, HashSet<string> visited,
        OpenCodeRequestOptions? requestOptions, CancellationToken cancellationToken)
    {
        await foreach (var child in client.EnumerateSessionsAsync(new SessionListRequest
        {
            ParentId = SessionParentFilter.Of(sessionId),
            Order = ListOrder.Descending,
            Limit = 200,
        }, requestOptions, cancellationToken).ConfigureAwait(false))
        {
            if (visited.Add(child.Id))
            {
                total += child.Cost;
                total = await ReadDescendantsAsync(child.Id, total, visited, requestOptions, cancellationToken).ConfigureAwait(false);
            }
        }

        return total;
    }
}
