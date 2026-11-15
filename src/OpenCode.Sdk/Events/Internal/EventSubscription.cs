using System.Runtime.ExceptionServices;
using System.Threading.Channels;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

/// <summary>A lightweight recipe or one enumerator's queue, tokens, pending move and first ending under the feed gate.</summary>
internal sealed class EventSubscription<T>(SharedEventFeed feed, EventSelection? selection,
    CancellationToken callToken, CancellationToken enumeratorToken, bool enumerableOnly = false) : IAsyncEnumerable<T>, IAsyncEnumerator<T> where T : class
{
    private readonly Channel<T>? _queue = selection is null || enumerableOnly ? null : Channel.CreateBounded<T>(
        new BoundedChannelOptions(EventFeedBounds.SubscriberCapacity)
        {
            FullMode = BoundedChannelFullMode.Wait,
            SingleReader = false,
            SingleWriter = false,
            AllowSynchronousContinuations = false,
        });
    private List<CancellationTokenRegistration>? _leases = enumerableOnly ? null : [];
    private T? _latest;
    private EventPendingMove<T>? _pending;
    private SubscriptionEnd? _end;
    private bool _started;
    private bool _admitted;
    private bool _disposed;
    private int _moving;

    public T Current { get; private set; } = null!;

    public int RegistrationCount
    {
        get
        {
            lock (feed.Gate)
            {
                return _leases?.Count ?? 0;
            }
        }
    }

    internal int RetainedCount
    {
        get
        {
            lock (feed.Gate)
            {
                return _queue?.Reader.Count ?? (_latest is null ? 0 : 1);
            }
        }
    }

    public IAsyncEnumerator<T> GetAsyncEnumerator(CancellationToken cancellationToken = default) =>
        new EventSubscription<T>(feed, selection, callToken, cancellationToken);

    public ValueTask<bool> MoveNextAsync()
    {
        if (Interlocked.CompareExchange(ref _moving, 1, 0) != 0)
        {
            throw new InvalidOperationException("An event enumeration cannot have concurrent moves.");
        }

        return MoveCoreAsync();
    }

    public ValueTask DisposeAsync()
    {
        feed.WithGate(work =>
        {
            _disposed = true;
            DrainLocked();
            EndLocked(new SubscriptionEnd(null, PreservesBacklog: false, IsSubscriberEnding: true), work);
        });
        return default;
    }

    /// <summary>The cheap first admission check; the authoritative live-token check still runs under the gate.</summary>
    internal void Precheck()
    {
        callToken.ThrowIfCancellationRequested();
        enumeratorToken.ThrowIfCancellationRequested();
    }

    internal CancellationTokenRegistration RegisterToken(CancellationToken token) =>
        token.UnsafeRegister(static state => ((EventSubscription<T>)state!).Cancel(), this);

    internal void AdoptLocked(CancellationTokenRegistration lease, AfterGate work)
    {
        if (_end is null && !_disposed)
        {
            (_leases ??= []).Add(lease);
        }
        else
        {
            work.Add(lease.Dispose);
        }
    }

    internal void MoveLocked(EventPendingMove<T> move, AfterGate work)
    {
        SelectLocked(move, offered: null, work);
        if (!move.IsSettled && !_admitted)
        {
            if (feed.IsDisposed)
            {
                EndLocked(new SubscriptionEnd(FailureClassification.DisposedClient(), false, false), work);
                return;
            }

            _admitted = true;
            feed.AdmitLocked(this, work);
        }
    }

    internal bool Includes(IEvent item) => selection is not null && selection.Includes(item.Type);

    internal void OfferLocked(T item, AfterGate work)
    {
        if (_end is not null || _disposed)
        {
            return;
        }

        if (_pending is { } pending)
        {
            SelectLocked(pending, item, work);
        }
        else if (TryObserveCancellation(out var token))
        {
            EndLocked(new SubscriptionEnd(Canceled(token), false, true), work);
        }
        else if (_queue is null)
        {
            _latest = item;
        }
        else if (!_queue.Writer.TryWrite(item))
        {
            EndLocked(new SubscriptionEnd(OpenCodeEventSubscriberOverflowException.Create(), false, true), work);
        }
    }

    internal void EndLocked(SubscriptionEnd ending, AfterGate work)
    {
        if (_end is null)
        {
            _end = ending;
            _queue?.Writer.TryComplete();
            if (!ending.PreservesBacklog)
            {
                DrainLocked();
            }

            if (_leases is { } leases)
            {
                foreach (var lease in leases)
                {
                    work.Add(lease.Dispose);
                }

                leases.Clear();
            }

            feed.RemoveLocked(this, ending, work);
        }

        if (_pending is { } pending)
        {
            _pending = null;
            SelectLocked(pending, offered: null, work);
        }
    }

    private async ValueTask<bool> MoveCoreAsync()
    {
        try
        {
            if (!_started)
            {
                _started = true;
                Precheck();
                RegisterTokens();
            }

            var completion = new TaskCompletionSource<Delivery<T>>(TaskCreationOptions.RunContinuationsAsynchronously);
            var move = new EventPendingMove<T>(completion);
            feed.WithGate(work => MoveLocked(move, work));
            var delivery = await completion.Task.ConfigureAwait(false);
            if (delivery.Error is { } error)
            {
                (delivery.Dispatch ?? ExceptionDispatchInfo.Capture(error)).Throw();
            }

            Current = delivery.Item!;
            return delivery.Item is not null;
        }
        finally
        {
            Volatile.Write(ref _moving, 0);
        }
    }

    private void RegisterTokens()
    {
        if (callToken.CanBeCanceled)
        {
            var lease = RegisterToken(callToken);
            feed.Adopt(this, lease);
        }

        if (enumeratorToken.CanBeCanceled && enumeratorToken != callToken)
        {
            var lease = RegisterToken(enumeratorToken);
            feed.Adopt(this, lease);
        }
    }

    private void Cancel() => feed.WithGate(work =>
    {
        if (TryObserveCancellation(out var token))
        {
            DrainLocked();
            EndLocked(new SubscriptionEnd(Canceled(token), false, true), work);
        }
    });

    private bool TryObserveCancellation(out CancellationToken token)
    {
        token = callToken.IsCancellationRequested ? callToken : enumeratorToken;
        return token.IsCancellationRequested;
    }

    private void SelectLocked(EventPendingMove<T> move, T? offered, AfterGate work)
    {
        if (move.IsSettled)
        {
            return;
        }

        if (TryObserveCancellation(out var token))
        {
            // Detach before ending: ending resolves a pending move through this same routine.
            _pending = null;
            DrainLocked();
            EndLocked(new SubscriptionEnd(Canceled(token), false, true), work);
            SettleLocked(move, new Delivery<T>(null, Canceled(token)));
        }
        else if (offered is null && TryTakeLocked(out var item))
        {
            SettleLocked(move, new Delivery<T>(item, null));
        }
        else if (_disposed || _end is not null)
        {
            SettleLocked(move, new Delivery<T>(null, _disposed ? null : _end!.Error, _disposed ? null : _end!.Dispatch));
        }
        else if (offered is not null)
        {
            SettleLocked(move, new Delivery<T>(offered, null));
        }
        else
        {
            _pending = move;
        }
    }

    private void SettleLocked(EventPendingMove<T> move, Delivery<T> delivery)
    {
        if (ReferenceEquals(_pending, move))
        {
            _pending = null;
        }

        _ = move.Settle(delivery);
    }

    private OperationCanceledException Canceled(CancellationToken token) => selection is null
        ? new OperationCanceledException(token)
        : new OperationCanceledException(FailureClassification.CanceledMessage(FailurePhase.EventStreamRead), token);

    private bool TryTakeLocked(out T? item)
    {
        if (_queue is not null)
        {
            return _queue.Reader.TryRead(out item);
        }

        item = _latest;
        _latest = null;
        return item is not null;
    }

    private void DrainLocked()
    {
        while (TryTakeLocked(out _))
        {
            // Only SourceFault keeps unread references; every discarding trigger releases them here.
        }
    }
}
