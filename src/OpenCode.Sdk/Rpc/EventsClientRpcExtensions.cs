using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>Typed subscriptions to events of a plugin's RPC definition.</summary>
public static class EventsClientRpcExtensions
{
    /// <summary>
    /// Subscribes to one event of a plugin's RPC definition, as opencode's own client does, yielding its typed data.
    /// </summary>
    /// <typeparam name="TData">The event's data type.</typeparam>
    /// <param name="client">The event family.</param>
    /// <param name="rpcEvent">The event's descriptor.</param>
    /// <param name="cancellationToken">The cancellation token; the enumerator token is also observed.</param>
    /// <returns>The lazy stream of the definition's events.</returns>
    /// <remarks>
    /// This is one more subscription on the client's shared event feed. It selects nothing at the feed,
    /// as upstream's facade does, so queued unrelated events count against its 4,096-event queue.
    /// Only the descriptor's event type is yielded. The stream skips server.connected markers;
    /// a consumer that must see every gap reads those markers on a separate subscription.
    /// </remarks>
    /// <exception cref="ArgumentNullException"><paramref name="client"/> or <paramref name="rpcEvent"/> is null.</exception>
    /// <exception cref="OpenCodeTransportException">An event's data does not match its data type, or reads as null.</exception>
    /// <exception cref="OpenCodeEventSubscriberOverflowException">The subscription's queue overflowed.</exception>
    /// <exception cref="OperationCanceledException">An entry token was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The root client was disposed.</exception>
    public static IAsyncEnumerable<RpcEventPayload<TData>> SubscribeAsync<TData>(
        this EventsClient client,
        RpcEventDefinition<TData> rpcEvent,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(rpcEvent);
        return new RpcEventReader(client).ReadAsync(rpcEvent, cancellationToken);
    }
}
