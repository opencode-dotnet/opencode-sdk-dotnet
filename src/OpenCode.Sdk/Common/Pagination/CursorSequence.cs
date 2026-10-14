using System.Runtime.CompilerServices;

namespace OpenCode.Sdk;

/// <summary>
/// The lazy cursor traversal a generated <c>Enumerate*Async</c> companion returns. Enumerating the
/// sequence itself yields the items of every page in order; <see cref="Pages"/> yields the same
/// traversal's page envelopes. Both doors drive the same traversal independently, so each
/// enumeration issues its own requests, and an API error surfaces when its page is reached.
/// </summary>
/// <typeparam name="TPage">The generated response envelope one page request returns.</typeparam>
/// <typeparam name="TItem">The item type the pages carry.</typeparam>
public sealed class CursorSequence<TPage, TItem> : IAsyncEnumerable<TItem>
    where TPage : OpenCodeResponse
{
    private readonly Func<CancellationToken, IAsyncEnumerable<TPage>> _pages;
    private readonly Func<TPage, IReadOnlyList<TItem>> _items;
    private readonly CancellationToken _sequenceToken;

    internal CursorSequence(
        Func<CancellationToken, IAsyncEnumerable<TPage>> pages,
        Func<TPage, IReadOnlyList<TItem>> items,
        CancellationToken sequenceToken)
    {
        ArgumentNullException.ThrowIfNull(pages);
        ArgumentNullException.ThrowIfNull(items);

        _pages = pages;
        _items = items;
        _sequenceToken = sequenceToken;
        Pages = new PageSequence(this);
    }

    /// <summary>
    /// Gets the traversal as page envelopes: every response the traversal requests, including its
    /// status, cursor, and the rest of the envelope. Enumerating it walks the pages again from the
    /// first request.
    /// </summary>
    public IAsyncEnumerable<TPage> Pages { get; }

    /// <summary>
    /// Gets an enumerator over the items of every page, in page order. The token supplied here and
    /// the token supplied to the companion are both observed.
    /// </summary>
    /// <param name = "cancellationToken">The cancellation token.</param>
    /// <returns>The item enumerator.</returns>
    public IAsyncEnumerator<TItem> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        EnumerateItemsAsync(_sequenceToken).GetAsyncEnumerator(cancellationToken);

    private async IAsyncEnumerable<TItem> EnumerateItemsAsync([EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var page in _pages(cancellationToken).ConfigureAwait(false))
        {
            foreach (var item in _items(page))
            {
                cancellationToken.ThrowIfCancellationRequested();
                yield return item;
            }
        }
    }

    /// <summary>
    /// The page door. It owns no traversal of its own: every enumeration asks the sequence for a
    /// fresh page walk, which is what makes the two doors independent.
    /// </summary>
    private sealed class PageSequence(CursorSequence<TPage, TItem> sequence) : IAsyncEnumerable<TPage>
    {
        public IAsyncEnumerator<TPage> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
            sequence._pages(sequence._sequenceToken).GetAsyncEnumerator(cancellationToken);
    }
}
