namespace OpenCode.Sdk.Internal;

/// <summary>One state enumeration, using the shared selection and token lifecycle with a coalescing mailbox.</summary>
internal sealed class EventStateWatcher(SharedEventFeed feed, CancellationToken callToken, CancellationToken enumeratorToken)
    : IAsyncEnumerable<EventConnectionState>, IAsyncEnumerator<EventConnectionState>
{
    private readonly EventSubscription<EventConnectionState> _subscription = new(feed, selection: null, callToken, enumeratorToken);

    public EventConnectionState Current => _subscription.Current;
    internal int RegistrationCount => _subscription.RegistrationCount;

    public IAsyncEnumerator<EventConnectionState> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new EventStateWatcher(feed, callToken, cancellationToken);

    public ValueTask<bool> MoveNextAsync() => _subscription.MoveNextAsync();

    public ValueTask DisposeAsync() => _subscription.DisposeAsync();
}
