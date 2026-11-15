using System.Collections.Concurrent;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Sessions;

/// <summary>Stored sessions with the attached server's live status and asks.</summary>
/// <param name="client">HQ's client.</param>
/// <param name="logger">Reports failures without ending the host.</param>
internal sealed class SessionBoard(OpenCodeClient client, ILogger<SessionBoard> logger) : BackgroundService
{
    private const int SessionLimit = 500;
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NeedsYouItem> _needsYou = new(StringComparer.Ordinal);
    private bool _ready;
    private bool _sourceConnected;
    private string? _connectionError;
    private string? _lastError;

    public event EventHandler? Changed;

    public bool Connected => _ready && _sourceConnected;
    public string? LastError
    {
        get => _lastError ?? _connectionError;
        private set => _lastError = value;
    }
    public IReadOnlyList<SessionRow> Sessions =>
    [
        .. _sessions.Values.OrderByDescending(static session => session.Time.Updated)
            .Select(session => new SessionRow(session, StatusOf(session.Id))),
    ];
    public IReadOnlyList<NeedsYouItem> NeedsYou => [.. _needsYou.Values.OrderBy(static item => item.Id, StringComparer.Ordinal)];

    public async Task AnswerAsync(NeedsYouItem item, PermissionReply decision, CancellationToken cancellationToken)
    {
        LastError = null;
        try
        {
            #region panel: reply
            _ = await client.Sessions.GetSessionClient(item.SessionId).ReplyToPermissionAsync(
                item.Id, new SessionPermissionReplyRequest { Decision = decision }, cancellationToken: cancellationToken);
            #endregion
            _ = _needsYou.TryRemove(item.Id, out _);
        }
        catch (OpenCodeApiException exception) when (exception.Error is PermissionNotFoundError)
        {
            _ = _needsYou.TryRemove(item.Id, out _);
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not answer permission {RequestId}.", item.Id);
            LastError = "Could not answer permission. Try again.";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task AnswerFormAsync(FormInfo form, SessionFormReplyRequest reply, CancellationToken cancellationToken)
    {
        LastError = null;
        try
        {
            #region panel: form-reply
            _ = await client.Sessions.GetSessionClient(form.SessionId).ReplyToFormAsync(form.Id, reply, cancellationToken: cancellationToken);
            #endregion
            _ = _needsYou.TryRemove(form.Id, out _);
        }
        catch (OpenCodeApiException exception) when (exception.Error is FormNotFoundError or FormAlreadySettledError)
        {
            _ = _needsYou.TryRemove(form.Id, out _);
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not answer form {FormId}.", form.Id);
            LastError = $"Could not answer the question: {exception.Message}";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        using var watchLifetime = CancellationTokenSource.CreateLinkedTokenSource(stoppingToken);
        var watching = WatchConnectionAsync(watchLifetime.Token);
        try
        {
            while (!stoppingToken.IsCancellationRequested)
            {
                try
                {
                    #region panel: subscribe
                    await using var events = client.Events.SubscribeAsync(cancellationToken: stoppingToken).GetAsyncEnumerator(stoppingToken);
                    #endregion
                    while (await events.MoveNextAsync())
                    {
                        // The admission marker refreshes first; the shared subscription buffers the idempotent dictionary updates.
                        await ApplyAsync(events.Current, stoppingToken);
                    }
                }
                catch (OpenCodeEventSubscriberOverflowException exception)
                {
                    logger.LogWarning(exception, "The board fell behind; admitting a new subscription before refreshing.");
                    _ready = false;
                    Changed?.Invoke(this, EventArgs.Empty);
                    continue;
                }
                catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
                {
                    return;
                }
                catch (Exception exception)
                {
                    logger.LogWarning(exception, "The board event subscription or refresh failed.");
                    LastError = exception.Message;
                }

                _ready = false;
                Changed?.Invoke(this, EventArgs.Empty);
                await Task.Delay(TimeSpan.FromSeconds(2), stoppingToken);
            }
        }
        finally
        {
            await watchLifetime.CancelAsync();
            await watching;
        }
    }

    private async Task WatchConnectionAsync(CancellationToken cancellationToken)
    {
        await foreach (var state in client.Events.WatchConnectionAsync(cancellationToken))
        {
            _sourceConnected = state.Status == EventConnectionStatus.Connected;
            _connectionError = state.Error?.Message;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    private string StatusOf(string sessionId)
    {
        if (_needsYou.Values.Any(item => item.SessionId == sessionId))
        {
            return "waiting for you";
        }

        return _running.ContainsKey(sessionId) ? "running" : "idle";
    }

    private async Task SeedAsync(CancellationToken cancellationToken)
    {
        _sessions.Clear();
        _running.Clear();
        _needsYou.Clear();
        await foreach (var session in client.Sessions.EnumerateSessionsAsync(
            new SessionListRequest { Order = ListOrder.Descending }, cancellationToken: cancellationToken))
        {
            _sessions[session.Id] = session;
            if (_sessions.Count == SessionLimit)
            {
                break;
            }
        }

        var active = await client.Sessions.GetActiveAsync(cancellationToken: cancellationToken);
        foreach (var sessionId in active.Active.Keys)
        {
            _running[sessionId] = 0;
        }

        foreach (var directory in Sessions.Where(row => _running.ContainsKey(row.Session.Id))
            .Select(static row => row.Session.Location.Directory).Distinct(StringComparer.Ordinal))
        {
            try
            {
                var location = new LocationSelector { Directory = directory };
                var asks = await client.Permissions.ListRequestsAsync(
                    new PermissionRequestListRequest { Location = location }, cancellationToken: cancellationToken);
                var forms = await client.Forms.ListFormsAsync(new FormListRequest { Location = location }, cancellationToken: cancellationToken);
                foreach (var ask in asks.Requests)
                {
                    _needsYou[ask.Id] = NeedsYouItem.FromPermission(ask);
                }

                foreach (var form in forms.Forms)
                {
                    _needsYou[form.Id] = NeedsYouItem.FromForm(form);
                }
            }
            catch (OpenCodeException exception)
            {
                logger.LogWarning(exception, "Skipping asks in unavailable directory {Directory}.", directory);
            }
        }
    }

    private async Task ApplyAsync(IEvent item, CancellationToken cancellationToken)
    {
        if (item.Type == "server.connected")
        {
            _ready = false;
            Changed?.Invoke(this, EventArgs.Empty);
            await SeedAsync(cancellationToken);
            LastError = null;
            _ready = true;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        switch (item)
        {
            case SessionCreated created:
                var read = await client.Sessions.GetSessionClient(created.Data.SessionId).GetAsync(cancellationToken: cancellationToken);
                _sessions[read.Session.Id] = read.Session;
                break;
            case SessionExecutionStarted started:
                _running[started.Data.SessionId] = 0;
                break;
            case SessionExecutionSucceeded or SessionExecutionFailed or SessionExecutionInterrupted:
                _ = _running.TryRemove(((ISessionScopedEvent)item).SessionId, out _);
                break;
            #region panel: asks
            case PermissionAsked asked:
                _needsYou[asked.Data.Id] = NeedsYouItem.FromAsked(asked.Data);
                break;
            #endregion
            case PermissionReplied replied:
                _ = _needsYou.TryRemove(replied.Data.RequestId, out _);
                break;
            case FormCreated formCreated:
                _needsYou[formCreated.Data.Form.Id] = NeedsYouItem.FromForm(formCreated.Data.Form);
                break;
            case FormReplied formReplied:
                _ = _needsYou.TryRemove(formReplied.Data.Id, out _);
                break;
            case FormCancelled formCancelled:
                _ = _needsYou.TryRemove(formCancelled.Data.Id, out _);
                break;
            default:
                return;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }
}
