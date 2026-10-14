namespace OpenCode.Sdk.Internal;

/// <summary>Projects one generated cursor-list operation onto the shared traversal core.</summary>
internal interface ICursorPageAdapter<TRequest, in TResponse, out TItem>
    where TRequest : ListRequest
    where TResponse : OpenCodeResponse
{
    /// <summary>Gets the ordered items carried by one successful page.</summary>
    public IReadOnlyList<TItem> GetItems(TResponse response);

    /// <summary>Gets the opaque next cursor, or <see langword="null"/> at the end of the traversal.</summary>
    public string? GetNextCursor(TResponse response);

    /// <summary>Creates a request for the opaque next cursor while retaining first-page settings that span pages.</summary>
    public TRequest CreateNextRequest(TRequest? initialRequest, string cursor);
}
