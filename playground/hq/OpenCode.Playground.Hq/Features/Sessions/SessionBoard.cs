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
    private const string RequestFailed = "Request failed";
    private readonly ConcurrentDictionary<string, SessionInfo> _sessions = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, byte> _running = new(StringComparer.Ordinal);
    private readonly ConcurrentDictionary<string, NeedsYouItem> _needsYou = new(StringComparer.Ordinal);
    private bool _ready;
    private bool _sourceConnected;
    private string? _connectionError;
    private string? _subscriptionError;
    private string? _lastError;

    public event EventHandler? Changed;

    public bool Connected => _ready && _sourceConnected;
    public string? LastError
    {
        get => _lastError ?? _subscriptionError ?? _connectionError;
        private set => _lastError = value;
    }
    public IReadOnlyList<SessionRow> Sessions =>
    [
        .. _sessions.Values.OrderByDescending(static session => session.Time.Updated)
            .Select(session => new SessionRow(session, StatusOf(session.Id))),
    ];
    public IReadOnlyList<NeedsYouItem> NeedsYou => [.. _needsYou.Values.OrderBy(static item => item.Id, StringComparer.Ordinal)];
    public SessionInfo? Find(string sessionId) => _sessions.GetValueOrDefault(sessionId);

    public Task DismissFormAsync(FormInfo form, CancellationToken cancellationToken) => SettleFormAsync(form, null, cancellationToken);

    public async Task ArchiveAsync(string sessionId, CancellationToken cancellationToken)
    {
        LastError = null;
        try
        {
            #region panel: archive
            _ = await client.Sessions.GetSessionClient(sessionId).UpdateAsync(
                new SessionUpdateRequest { Archived = true }, cancellationToken: cancellationToken);
            #endregion
            RemoveSession(sessionId);
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not archive session {SessionId}.", sessionId);
            LastError = RequestFailed;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task<bool> DeleteAsync(string sessionId, CancellationToken cancellationToken)
    {
        LastError = null;
        var deleted = false;
        try
        {
            #region panel: delete
            _ = await client.Sessions.GetSessionClient(sessionId).RemoveAsync(cancellationToken: cancellationToken);
            #endregion
            RemoveSession(sessionId);
            deleted = true;
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not delete session {SessionId}.", sessionId);
            LastError = "Failed to delete session";
        }

        Changed?.Invoke(this, EventArgs.Empty);
        return deleted;
    }

    private void RemoveSession(string sessionId)
    {
        _ = _sessions.TryRemove(sessionId, out _);
        _ = _running.TryRemove(sessionId, out _);
        foreach (var item in _needsYou.Values.Where(item => item.SessionId == sessionId))
        {
            _ = _needsYou.TryRemove(item.Id, out _);
        }
    }

    /// <summary>Adds a session this app made, as upstream's app does with a fork it opens.</summary>
    /// <remarks>The server announces a fork with <c>session.forked</c>, not <c>session.created</c>, so no event adds it.</remarks>
    /// <param name="session">The session the server returned.</param>
    public void Remember(SessionInfo session)
    {
        _sessions[session.Id] = session;
        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task RenameAsync(string sessionId, string title, CancellationToken cancellationToken)
    {
        LastError = null;
        try
        {
            #region panel: rename
            _ = await client.Sessions.GetSessionClient(sessionId).UpdateAsync(
                new SessionUpdateRequest { Title = title }, cancellationToken: cancellationToken);
            #endregion
            if (_sessions.TryGetValue(sessionId, out var session))
            {
                _sessions[sessionId] = session with { Title = title };
            }
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not rename session {SessionId}.", sessionId);
            LastError = RequestFailed;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    /// <summary>Replaces one session's asks with the ones the server still lists as pending.</summary>
    /// <param name="sessionId">The session whose asks are listed.</param>
    /// <param name="cancellationToken">Ends the listing.</param>
    /// <returns>A task that completes once the board holds the server's list.</returns>
    public async Task ResyncAsync(string sessionId, CancellationToken cancellationToken)
    {
        // Asks that arrive by event while the lists are read are not in the snapshot, so they stay.
        var known = _needsYou.Values.Where(item => item.SessionId == sessionId).Select(static item => item.Id).ToArray();
        NeedsYouItem[] pending;
        try
        {
            var session = client.Sessions.GetSessionClient(sessionId);
            var asks = await session.ListRequestsAsync(cancellationToken: cancellationToken);
            var forms = await session.ListFormsAsync(cancellationToken: cancellationToken);
            pending = [.. asks.Requests.Select(NeedsYouItem.FromPermission), .. forms.Forms.Select(NeedsYouItem.FromForm)];
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not list the asks of session {SessionId}.", sessionId);
            return;
        }

        foreach (var id in known.Except(pending.Select(static item => item.Id), StringComparer.Ordinal))
        {
            _ = _needsYou.TryRemove(id, out _);
        }

        foreach (var item in pending)
        {
            _needsYou[item.Id] = item;
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task AnswerAsync(NeedsYouItem item, PermissionReply decision, CancellationToken cancellationToken)
    {
        LastError = null;
        try
        {
            #region panel: reply
            // The card supplies PermissionReply.Reject, PermissionReply.Always, or PermissionReply.Once.
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

    public Task AnswerFormAsync(FormInfo form, SessionFormReplyRequest reply, CancellationToken cancellationToken) =>
        SettleFormAsync(form, reply, cancellationToken);

    private async Task SettleFormAsync(FormInfo form, SessionFormReplyRequest? reply, CancellationToken cancellationToken)
    {
        LastError = null;
        try
        {
            if (reply is null)
            {
                #region panel: form-dismiss
                _ = await client.Sessions.GetSessionClient(form.SessionId).CancelFormAsync(form.Id, cancellationToken: cancellationToken);
                #endregion
            }
            else
            {
                #region panel: form-reply
                _ = await client.Sessions.GetSessionClient(form.SessionId).ReplyToFormAsync(form.Id, reply, cancellationToken: cancellationToken);
                #endregion
            }
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
                    _subscriptionError = exception.Message;
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
        try
        {
            await foreach (var state in client.Events.WatchConnectionAsync(cancellationToken))
            {
                _sourceConnected = state.Status == EventConnectionStatus.Connected;
                _connectionError = state.Error?.Message;
                Changed?.Invoke(this, EventArgs.Empty);
            }

            if (!cancellationToken.IsCancellationRequested)
            {
                throw new InvalidOperationException("The event connection watch ended before shutdown.");
            }
        }
        catch (Exception exception) when (!cancellationToken.IsCancellationRequested)
        {
            logger.LogWarning(exception, "The board connection watch failed.");
            _sourceConnected = false;
            _connectionError = exception.Message;
            Changed?.Invoke(this, EventArgs.Empty);
        }
    }

    public string StatusOf(string sessionId)
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
            new SessionListRequest { Order = ListOrder.Descending, Archived = false }, cancellationToken: cancellationToken))
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
            _subscriptionError = null;
            _ready = true;
            Changed?.Invoke(this, EventArgs.Empty);
            return;
        }

        switch (item)
        {
            case SessionCreated or SessionUnarchived:
                var added = ((ISessionScopedEvent)item).SessionId;
                var read = await client.Sessions.GetSessionClient(added).GetAsync(cancellationToken: cancellationToken);
                _sessions[read.Session.Id] = read.Session;
                break;
            case SessionDeleted or SessionArchived:
                RemoveSession(((ISessionScopedEvent)item).SessionId);
                break;
            case SessionRenamed renamed when _sessions.TryGetValue(renamed.Data.SessionId, out var session):
                _sessions[session.Id] = session with { Title = renamed.Data.Title };
                break;
            case SessionAgentSelected selected when _sessions.TryGetValue(selected.Data.SessionId, out var session):
                _sessions[session.Id] = session with { Agent = selected.Data.Agent };
                break;
            case SessionModelSelected selected when _sessions.TryGetValue(selected.Data.SessionId, out var session):
                _sessions[session.Id] = session with { Model = selected.Data.Model };
                break;
            case SessionExecutionStarted started:
                _running[started.Data.SessionId] = 0;
                break;
            case SessionExecutionSucceeded or SessionExecutionFailed or SessionExecutionInterrupted:
                var ended = ((ISessionScopedEvent)item).SessionId;
                _ = _running.TryRemove(ended, out _);
                // A turn that ends drops its pending asks without an event, so the board re-lists them (README, Architecture).
                // A shutdown interruption is skipped, as upstream's client skips it.
                if (item is not SessionExecutionInterrupted interrupted || interrupted.Data.Reason != SessionExecutionInterruptedDataReason.Shutdown)
                {
                    await ResyncAsync(ended, cancellationToken);
                }

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
