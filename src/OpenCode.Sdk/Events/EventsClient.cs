using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk;

/// <summary>
/// Experimental event stream routes.
/// </summary>
public class EventsClient
{
    private readonly SharedEventFeed? _feed;

    internal EventsClient(Pipeline pipeline)
    {
        ArgumentNullException.ThrowIfNull(pipeline);
        var feed = new SharedEventFeed(pipeline);
        pipeline.RegisterOwned(feed);
        _feed = feed;
    }

    /// <summary>
    /// Initializes a mocking instance; members invoked without an override throw an instructive failure.
    /// </summary>
    protected EventsClient()
    {
    }

    /// <summary>
    /// Subscribe to events. Subscribe to native events and plugin RPC events across all server locations. Volatile by contract: a slow consumer overflows and fails the stream, and events during disconnection are missed.
    /// </summary>
    /// <param name="request">The local type selection, captured at the call; the connection reads the whole bus.</param>
    /// <param name="requestOptions">NoThrow, NetworkTimeout and Headers are refused. Location is accepted but the shared connection sends only the client's ambient location; client-wide headers reach it.</param>
    /// <param name="cancellationToken">The cancellation token.</param>
    /// <returns>The &apos;IEvent&apos; stream.</returns>
    /// <exception cref="OpenCodeEventSubscriberOverflowException">The next selected offer exceeded the subscription's 4,096 queued events; its backlog was discarded.</exception>
    /// <exception cref="OperationCanceledException">An entry token was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The root client was disposed.</exception>
    /// <remarks>
    /// Operation <c>event.subscribe</c>: <c>GET /api/event</c>.
    /// Each enumeration is an independent subscription to one shared background source per client.
    /// The first <c>server.connected</c> marks admission, not liveness; earlier business events are not replayed.
    /// A later marker announces a possible gap: refresh authoritative state. Event objects are shared references; treat them as read-only.
    /// A subscription retains at most 4,096 queued selected events. Overflow ends only that subscription and discards its backlog.
    /// The source retries ordinary attempt failures forever, 1 s apart without backoff; connection state reports the last classified failure.
    /// An escaping source fault preserves each subscription's queued prefix, then throws the same exception.
    /// Live cancellation wins over unread data and stored endings, but never changes an already settled move.
    /// Enumerator disposal discards unread data and later pulls end false unless an entry token is cancelled.
    /// </remarks>
    public virtual IAsyncEnumerable<IEvent> SubscribeAsync(EventSubscribeRequest? request = null,
        OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
    {
        var feed = Feed;
        feed.ThrowIfDisposed();
        ErrorBehaviorPolicy.RefuseNoThrow(requestOptions, "Streams", ErrorBehaviorPolicy.EventSubscriptionNoThrowMessage);
        if (requestOptions?.NetworkTimeout is not null)
        {
            throw new ArgumentException(FailureClassification.EventStreamNetworkTimeoutMessage, nameof(requestOptions));
        }

        if (requestOptions?.Headers is not null)
        {
            throw new ArgumentException(FailureClassification.EventStreamHeadersMessage, nameof(requestOptions));
        }

        return feed.Subscribe(EventSelection.Capture(request?.Types, nameof(request)), cancellationToken);
    }

    /// <summary>Gets the shared source's current snapshot without opening a connection.</summary>
    /// <returns>The current source-level state; it is not a subscription position or liveness proof.</returns>
    /// <exception cref="ObjectDisposedException">The root client was disposed.</exception>
    public virtual EventConnectionState GetConnectionState() => Feed.GetConnectionState();

    /// <summary>Watches the initial source snapshot and then the latest unread snapshots, coalescing intermediate changes.</summary>
    /// <param name="cancellationToken">The cancellation token; the enumerator token is also observed.</param>
    /// <returns>A lazy state watch that never opens or keeps alive an event source.</returns>
    /// <exception cref="OperationCanceledException">An entry token was cancelled.</exception>
    /// <exception cref="ObjectDisposedException">The root client was disposed.</exception>
    public virtual IAsyncEnumerable<EventConnectionState> WatchConnectionAsync(CancellationToken cancellationToken = default)
    {
        var feed = Feed;
        feed.ThrowIfDisposed();
        return feed.Watch(cancellationToken);
    }

    private SharedEventFeed Feed => _feed ?? throw MockSeam.CreateError("EventsClient", "Pipeline");
}
