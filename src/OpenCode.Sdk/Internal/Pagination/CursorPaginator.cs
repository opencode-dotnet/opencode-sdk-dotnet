using System.Runtime.CompilerServices;

namespace OpenCode.Sdk.Internal;

/// <summary>Lazily traverses generated cursor-list operations through their ordinary one-page methods.</summary>
internal static class CursorPaginator
{
    /// <summary>Creates a lazy sequence over the operation's opaque next cursors.</summary>
    public static CursorSequence<TResponse, TItem> EnumerateAsync<TRequest, TResponse, TItem>(
        Func<TRequest?, OpenCodeRequestOptions?, CancellationToken, Task<TResponse>> fetchPage,
        TRequest? initialRequest,
        ICursorPageAdapter<TRequest, TResponse, TItem> adapter,
        CancellationToken cancellationToken)
        where TRequest : ListRequest
        where TResponse : OpenCodeResponse
    {
        ArgumentNullException.ThrowIfNull(fetchPage);
        ArgumentNullException.ThrowIfNull(adapter);

        return new CursorSequence<TResponse, TItem>(
            token => EnumeratePagesCoreAsync(fetchPage, initialRequest, adapter, token),
            adapter.GetItems,
            cancellationToken);
    }

    /// <summary>
    /// The single traversal: one page per request, the next request built from the initial one and
    /// the opaque returned cursor, and an absent next cursor as the only end signal.
    /// </summary>
    private static async IAsyncEnumerable<TResponse> EnumeratePagesCoreAsync<TRequest, TResponse, TItem>(
        Func<TRequest?, OpenCodeRequestOptions?, CancellationToken, Task<TResponse>> fetchPage,
        TRequest? initialRequest,
        ICursorPageAdapter<TRequest, TResponse, TItem> adapter,
        [EnumeratorCancellation] CancellationToken cancellationToken)
        where TRequest : ListRequest
        where TResponse : OpenCodeResponse
    {
        var request = initialRequest;
        while (true)
        {
            var page = await fetchPage(request, null, cancellationToken).ConfigureAwait(false);
            yield return page;

            if (adapter.GetNextCursor(page) is not { } cursor)
            {
                yield break;
            }

            request = adapter.CreateNextRequest(initialRequest, cursor);
        }
    }
}
