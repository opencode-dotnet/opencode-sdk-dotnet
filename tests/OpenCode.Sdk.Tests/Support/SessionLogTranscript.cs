using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Support;

internal sealed class SessionLogTranscript
{
    private static readonly TimeSpan FollowWindow = TimeSpan.FromSeconds(120);
    private readonly List<ISessionLogItem> _liveItems = [];
    private readonly List<ISessionLogItem> _replayItems = [];
    private readonly SessionClient? _session;
    private readonly EventDiagnosticSummary _observed = new();
    private CancellationToken _callerToken;
    private IAsyncEnumerator<ISessionLogItem>? _enumerator;
    private CancellationTokenSource? _window;

    public SessionLogTranscript(SessionClient session)
    {
        ArgumentNullException.ThrowIfNull(session);
        _session = session;
    }

    internal SessionLogTranscript(
        IAsyncEnumerator<ISessionLogItem> enumerator,
        CancellationTokenSource window)
    {
        ArgumentNullException.ThrowIfNull(enumerator);
        ArgumentNullException.ThrowIfNull(window);
        _enumerator = enumerator;
        _window = window;
    }

    public IReadOnlyList<ISessionLogItem> LiveItems => _liveItems;

    public EventLogSynced? Marker { get; private set; }

    public IReadOnlyList<ISessionLogItem> ReplayItems => _replayItems;

    public SessionExecutionSucceeded? Succeeded { get; private set; }

    public SessionTextEnded? TextEnded { get; private set; }

    public async Task<List<ISessionLogItem>> ReadToEndAsync(
        SessionLogRequest request,
        CancellationToken cancellationToken)
    {
        var items = new List<ISessionLogItem>();
        var observed = new EventDiagnosticSummary();
        try
        {
            await foreach (var item in Session.GetLogAsync(request, cancellationToken))
            {
                items.Add(item);
                observed.Add(item.Type);
            }
        }
        catch (Exception exception)
        {
            exception.Data[EventDiagnosticSummary.DataKey] = observed.ToString();
            throw;
        }

        return items;
    }

    public async Task AttachAsync(long after, CancellationToken cancellationToken)
    {
        if (_enumerator is not null)
        {
            throw new InvalidOperationException("The session log transcript is already attached.");
        }

        _callerToken = cancellationToken;
        _window = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        _window.CancelAfter(FollowWindow);
        _enumerator = Session.GetLogAsync(
                new SessionLogRequest { After = after, Follow = QueryBoolean.True },
                _window.Token)
            .GetAsyncEnumerator(_window.Token);
        while (await MoveNextAsync("the log.synced attachment marker"))
        {
            if (_enumerator.Current is EventLogSynced marker)
            {
                Marker = marker;
                return;
            }

            _replayItems.Add(_enumerator.Current);
        }

        throw EndedBefore("the log.synced attachment marker");
    }

    public async Task ReadTurnAsync(string sessionId, string reply)
    {
        if (Marker is null || _enumerator is null)
        {
            throw new InvalidOperationException("The session log transcript is not attached.");
        }

        var sawOwnedText = false;
        while (await MoveNextAsync("the followed turn completed"))
        {
            var item = _enumerator.Current;
            _liveItems.Add(item);
            if (item is SessionTextEnded text
                && text.Data.SessionId == sessionId
                && text.Data.Text == reply)
            {
                TextEnded = text;
                sawOwnedText = true;
                continue;
            }

            if (sawOwnedText
                && item is SessionExecutionSucceeded succeeded
                && succeeded.Data.SessionId == sessionId)
            {
                Succeeded = succeeded;
                return;
            }
        }

        throw EndedBefore("the followed turn completed");
    }

    public async Task DisposeAsync(CancellationToken _)
    {
        var enumerator = Interlocked.Exchange(ref _enumerator, null);
        var window = Interlocked.Exchange(ref _window, null);
        if (window is null)
        {
            return;
        }

        try
        {
            if (enumerator is not null)
            {
                await enumerator.DisposeAsync();
            }
        }
        finally
        {
            window.Dispose();
        }
    }

    private SessionClient Session =>
        _session ?? throw new InvalidOperationException("The injected transcript has no session client.");

    private async Task<bool> MoveNextAsync(string target)
    {
        var enumerator = _enumerator
                         ?? throw new InvalidOperationException("The session log transcript is not attached.");
        try
        {
            var moved = await enumerator.MoveNextAsync();
            if (moved)
            {
                _observed.Add(enumerator.Current.Type);
            }

            return moved;
        }
        catch (OperationCanceledException exception)
            when (!_callerToken.IsCancellationRequested && _window!.IsCancellationRequested)
        {
            throw new TimeoutException(
                $"The session log timed out before {target}. Items: {_observed}.",
                exception);
        }
        catch (Exception exception)
        {
            exception.Data[EventDiagnosticSummary.DataKey] = _observed.ToString();
            throw;
        }
    }

    private InvalidOperationException EndedBefore(string target) =>
        new($"The session log ended before {target}. Items: {_observed}.");
}
