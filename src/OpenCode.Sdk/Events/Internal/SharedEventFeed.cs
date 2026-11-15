using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

/// <summary>One client-wide source fence, publication gate and subscription registry.</summary>
internal sealed class SharedEventFeed(IEventAttemptSource attempts, TimeProvider time, TimeSpan idleWindow,
    IEventFeedGateProbe probe) : IDisposable
{
    private readonly HashSet<EventSubscription<IEvent>> _subscriptions = [];
    private readonly HashSet<EventSubscription<EventConnectionState>> _watchers = [];
    private readonly Dictionary<EventSource, Task> _running = [];
    private readonly EventSourceRunner _runner = new(attempts, time, idleWindow);
    private EventSource? _current;
    private IEvent? _marker;
    private EventConnectionState _state = new(EventConnectionStatus.Idle, 0, null);

    internal SharedEventFeed(Pipeline pipeline)
        : this(new EventAttemptSource(pipeline), pipeline.Connection.Time, pipeline.IdleWindow, new NoOpEventFeedGateProbe())
    {
    }

    internal Lock Gate { get; } = new();
    internal bool IsDisposed { get; private set; }

    internal int WatcherCount
    {
        get
        {
            lock (Gate)
            {
                return _watchers.Count;
            }
        }
    }

    public void ThrowIfDisposed()
    {
        lock (Gate)
        {
            if (IsDisposed)
            {
                throw FailureClassification.DisposedClient();
            }
        }
    }

    public async Task JoinAsync()
    {
        Task completion;
        lock (Gate)
        {
            completion = Task.WhenAll(_running.Values);
        }

        await completion.ConfigureAwait(false);
    }

    internal EventSource? GetCurrentSource()
    {
        lock (Gate)
        {
            return _current;
        }
    }

    public IAsyncEnumerable<IEvent> Subscribe(EventSelection selection, CancellationToken cancellationToken) =>
        new EventSubscription<IEvent>(this, selection, cancellationToken, default);

    public EventConnectionState GetConnectionState()
    {
        lock (Gate)
        {
            if (IsDisposed)
            {
                throw FailureClassification.DisposedClient();
            }

            return _state;
        }
    }

    public IAsyncEnumerable<EventConnectionState> Watch(CancellationToken cancellationToken) =>
        new EventStateWatcher(this, cancellationToken, default);

    internal void WithGate(Action<AfterGate> section)
    {
        var work = new AfterGate();
        work.Run(() =>
        {
            lock (Gate)
            {
                section(work);
            }
        });
    }

    internal void Adopt<T>(EventSubscription<T> subscription, CancellationTokenRegistration lease) where T : class =>
        WithGate(work => subscription.AdoptLocked(lease, work));

    internal void AdmitLocked<T>(EventSubscription<T> subscription, AfterGate work) where T : class
    {
        if (subscription is EventSubscription<IEvent> events)
        {
            _ = _subscriptions.Add(events);
            if (_current is null)
            {
                StartLocked(work);
            }

            if (_marker is { } marker)
            {
                events.OfferLocked(marker, work);
            }
        }
        else if (subscription is EventSubscription<EventConnectionState> watcher)
        {
            _ = _watchers.Add(watcher);
            watcher.OfferLocked(_state, work);
        }
    }

    internal void RemoveLocked<T>(EventSubscription<T> subscription, SubscriptionEnd ending, AfterGate work) where T : class
    {
        if (subscription is EventSubscription<IEvent> events)
        {
            _ = _subscriptions.Remove(events);
            if (_subscriptions.Count == 0 && ending.IsSubscriberEnding)
            {
                DetachLocked(work);
            }
        }
        else if (subscription is EventSubscription<EventConnectionState> watcher)
        {
            _ = _watchers.Remove(watcher);
        }
    }

    public bool RecordAttemptStarted(EventSource source)
    {
        lock (Gate)
        {
            return !IsDisposed && ReferenceEquals(_current, source);
        }
    }

    public void RecordConnected(EventSource source, IEvent marker) => WithGate(work =>
    {
        if (IsDisposed || !ReferenceEquals(_current, source))
        {
            return;
        }

        _marker = marker;
        SetStateLocked(new EventConnectionState(EventConnectionStatus.Connected, 0, null), work);
        PublishLocked(marker, work);
    });

    public void Publish(EventSource source, IEvent item) => WithGate(work =>
    {
        if (!IsDisposed && ReferenceEquals(_current, source))
        {
            probe.OnPublication();
            PublishLocked(item, work);
        }
    });

    public void RecordDrop(EventSource source, Exception failure, int attempt) => WithGate(work =>
    {
        if (!IsDisposed && ReferenceEquals(_current, source))
        {
            _marker = null;
            SetStateLocked(new EventConnectionState(EventConnectionStatus.Reconnecting, attempt, failure), work);
        }
    });

    public void RecordFault(EventSource source, Exception exception) => WithGate(work =>
    {
        if (IsDisposed || !ReferenceEquals(_current, source))
        {
            return;
        }

        DetachLocked(work);
        foreach (var subscription in _subscriptions.ToArray())
        {
            subscription.EndLocked(new SubscriptionEnd(exception, true, false), work);
        }
    });

    public void Dispose()
    {
        var work = new AfterGate();
        EventSource? source = null;
        try
        {
            lock (Gate)
            {
                if (IsDisposed)
                {
                    return;
                }

                IsDisposed = true;
                source = _current;
                _current = null;
                _marker = null;
                foreach (var subscription in _subscriptions.ToArray())
                {
                    subscription.EndLocked(new SubscriptionEnd(FailureClassification.DisposedClient(), false, false), work);
                }

                foreach (var watcher in _watchers.ToArray())
                {
                    watcher.EndLocked(new SubscriptionEnd(FailureClassification.DisposedClient(), false, false), work);
                }
            }
        }
        catch (Exception exception)
        {
            work.RecordFailure(exception);
        }
        finally
        {
            try
            {
                source?.Dispose();
            }
            catch (Exception exception)
            {
                work.RecordFailure(exception);
            }
            finally
            {
                work.Run();
            }
        }
    }

    private void StartLocked(AfterGate work)
    {
        var source = new EventSource();
        _current = source;
        SetStateLocked(new EventConnectionState(EventConnectionStatus.Connecting, 0, null), work);
        var completion = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        _running.Add(source, completion.Task);
        void Finish(EventSource finished)
        {
            lock (Gate)
            {
                _ = _running.Remove(finished);
                completion.SetResult();
            }
        }

        if (ExecutionContext.IsFlowSuppressed())
        {
            source.Start(_runner, this, Finish);
        }
        else
        {
            using (ExecutionContext.SuppressFlow())
            {
                source.Start(_runner, this, Finish);
            }
        }
    }

    internal void Complete(EventSource source, Task task)
    {
        var failure = task.Exception?.InnerException ?? (task.IsCanceled
            ? new TaskCanceledException(task)
            : new InvalidOperationException("The current event source returned without an ending."));
        RecordFault(source, failure);
    }

    private void DetachLocked(AfterGate work)
    {
        if (_current is { } source)
        {
            _current = null;
            _marker = null;
            SetStateLocked(new EventConnectionState(EventConnectionStatus.Idle, 0, null), work);
            work.Add(new StopWork(source).Run);
        }
    }

    private void PublishLocked(IEvent item, AfterGate work)
    {
        foreach (var subscription in _subscriptions.Where(subscription => subscription.Includes(item)).ToArray())
        {
            subscription.OfferLocked(item, work);
        }
    }

    private void SetStateLocked(EventConnectionState state, AfterGate work)
    {
        _state = state;
        foreach (var watcher in _watchers.ToArray())
        {
            watcher.OfferLocked(state, work);
        }
    }
}
