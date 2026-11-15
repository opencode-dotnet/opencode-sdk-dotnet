namespace OpenCode.Sdk.Internal;

/// <summary>A lightweight watch recipe or one state enumeration with a coalescing mailbox and token lifecycle.</summary>
internal sealed class EventStateWatcher(SharedEventFeed feed, CancellationToken callToken, CancellationToken enumeratorToken,
    bool enumerableOnly = false)
    : IAsyncEnumerable<EventConnectionState>, IAsyncEnumerator<EventConnectionState>
{
    private readonly EventSubscription<EventConnectionState>? _subscription = enumerableOnly
        ? null : new(feed, selection: null, callToken, enumeratorToken);

    public EventConnectionState Current => _subscription!.Current;
    internal int RegistrationCount => _subscription?.RegistrationCount ?? 0;

    public IAsyncEnumerator<EventConnectionState> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new EventStateWatcher(feed, callToken, cancellationToken);

    public ValueTask<bool> MoveNextAsync()
    {
        if (enumerableOnly)
        {
            throw new InvalidOperationException(EventSubscription<EventConnectionState>.EnumerateRecipeMessage);
        }

        return _subscription!.MoveNextAsync();
    }

    public ValueTask DisposeAsync() => _subscription?.DisposeAsync() ?? default;
}
