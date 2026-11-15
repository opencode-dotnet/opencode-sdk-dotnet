namespace OpenCode.Sdk.Internal;

/// <summary>A generation's runner and worker-cancellation owner; both are observed before its CTS is released.</summary>
internal sealed class EventSource : IDisposable
{
    private readonly Lock _gate = new();
    private readonly CancellationTokenSource _stop = new();
    private Action<EventSource>? _finished;
    private bool _runnerDone;
    private bool _cancelDone = true;
    private bool _requested;
    private bool _released;

    public EventSource() => Token = _stop.Token;

    public CancellationToken Token { get; }
    public Task? RunnerObservation { get; private set; }
    public Task? CancellationObservation { get; private set; }
    public AggregateException? CancellationFailure { get; private set; }

    public bool IsReleased
    {
        get
        {
            lock (_gate)
            {
                return _released;
            }
        }
    }

    public void Start(EventSourceRunner runner, SharedEventFeed feed, Action<EventSource> finished)
    {
        _finished = finished;
        var running = Task.Run(() => runner.RunAsync(feed, this), CancellationToken.None);
        var publication = running.ContinueWith(task => feed.Complete(this, task),
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        RunnerObservation = publication.ContinueWith(task =>
        {
            // A retired completion callback has no subscriber to reach; its failure is observed and dropped.
            _ = task.Exception;
            lock (_gate)
            {
                _runnerDone = true;
            }

            Dispose();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    public void RequestStop()
    {
        lock (_gate)
        {
            if (_requested || _released)
            {
                return;
            }

            _requested = true;
            _cancelDone = false;
        }

        var canceling = _stop.CancelOnWorkerAsync();
        CancellationObservation = canceling.ContinueWith(task =>
        {
            // Stop follows logical retirement. A callback failure is recorded, observed and dropped, not published to a replacement.
            CancellationFailure = task.Exception?.Flatten();
            lock (_gate)
            {
                _cancelDone = true;
            }

            Dispose();
        }, CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
    }

    /// <summary>Requests shutdown, releasing the CTS only after both owned operations have been observed.</summary>
    public void Dispose()
    {
        RequestStop();
        lock (_gate)
        {
            if (_released || !_runnerDone || !_cancelDone)
            {
                return;
            }

            _released = true;
            _stop.Dispose();
        }

        _finished!(this);
    }
}
