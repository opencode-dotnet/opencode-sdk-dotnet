using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Conversation;

/// <summary>One session's stored conversation and live turn.</summary>
internal sealed class SessionConversation(OpenCodeClient client, ILogger<SessionConversation> logger)
{
    private readonly List<ConversationLine> _lines = [];
    private readonly Dictionary<(string AssistantMessageId, long Ordinal), int> _live = [];
    private readonly HashSet<string> _outbox = [with(StringComparer.Ordinal)];
    private bool _executing;
    private int _sending;
    private string? _choicesFolder;
    private int _choicesLoad;

    public event EventHandler? Changed;
    public string SessionId { get; private set; } = "";
    public IReadOnlyList<ConversationLine> Lines => _lines;
    /// <summary>Gets whether the server runs a turn or a prompt is on its way; each owns its own part.</summary>
    public bool Running => _executing || _sending > 0;
    public bool Ready { get; private set; }
    public int EndedTurns { get; private set; }
    public string? LastError { get; private set; }
    public IReadOnlyList<AgentInfo> Agents { get; private set; } = [];
    public IReadOnlyDictionary<ModelRef, string> Models { get; private set; } = new Dictionary<ModelRef, string>();
    public string? Agent { get; private set; }
    public ModelRef? Model { get; private set; }

    public async Task LoadChoicesAsync(CancellationToken cancellationToken)
    {
        var load = ++_choicesLoad;
        LastError = null;
        try
        {
            #region panel: choices
            var current = await client.Sessions.GetSessionClient(SessionId).GetAsync(cancellationToken: cancellationToken);
            _choicesFolder = current.Session.Location.Directory;
            var location = new LocationSelector { Directory = current.Session.Location.Directory };
            var agents = await client.Agents.ListAgentsAsync(new AgentListRequest { Location = location }, cancellationToken: cancellationToken);
            var providers = await client.Providers.ListProvidersAsync(new ProviderListRequest { Location = location }, cancellationToken: cancellationToken);
            var models = await client.LanguageModels.ListModelsAsync(new ModelListRequest { Location = location }, cancellationToken: cancellationToken);
            var names = providers.Providers.ToDictionary(static provider => provider.Id, static provider => provider.Name, StringComparer.Ordinal);
            var model = current.Session.Model;
            if (model is null)
            {
                var chosen = await client.LanguageModels.GetDefaultAsync(new ModelDefaultRequest { Location = location }, cancellationToken: cancellationToken);
                model = chosen.Default is { } found ? new ModelRef { Id = found.Id, ProviderId = found.ProviderId } : null;
            }
            #endregion
            if (load != _choicesLoad)
            {
                // As in upstream's client, a read that a newer one replaced does not commit.
                return;
            }

            Agents = [.. agents.Agents.Where(static agent => agent.Mode != AgentInfoMode.Subagent && !agent.Hidden)];
            Models = models.Models.Where(listed => listed.Status != ModelInfoStatus.Deprecated && names.ContainsKey(listed.ProviderId))
                .ToDictionary(static listed => new ModelRef { Id = listed.Id, ProviderId = listed.ProviderId }, listed => $"{listed.Name} ({names[listed.ProviderId]})");
            Agent = current.Session.Agent ?? (Agents.Count > 0 ? Agents[0].Id : null);
            Model = model;
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not load choices for session {SessionId}.", SessionId);
            if (load != _choicesLoad)
            {
                return;
            }

            LastError = "Request failed";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    public async Task CompactAsync(ModelRef? model, CancellationToken cancellationToken)
    {
        LastError = null;
        try
        {
            await SwitchAsync(null, model, cancellationToken);
            #region panel: compact
            _ = await client.Sessions.GetSessionClient(SessionId).CompactAsync(
                new SessionCompactRequest { Id = $"msg_{Guid.NewGuid():N}" }, cancellationToken: cancellationToken);
            #endregion
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not compact session {SessionId}.", SessionId);
            LastError = "Request failed";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private async Task SwitchAsync(string? agent, ModelRef? model, CancellationToken cancellationToken)
    {
        #region panel: switch
        var session = client.Sessions.GetSessionClient(SessionId);
        var current = await session.GetAsync(cancellationToken: cancellationToken);
        if (agent is not null && agent != current.Session.Agent)
        {
            _ = await session.SwitchAgentAsync(new SessionSwitchAgentRequest { Agent = agent }, cancellationToken: cancellationToken);
        }

        if (model is not null && model != current.Session.Model)
        {
            _ = await session.SwitchModelAsync(new SessionSwitchModelRequest { Model = model }, cancellationToken: cancellationToken);
        }
        #endregion
    }

    public async Task<SessionInfo?> ForkAsync(string messageId, CancellationToken cancellationToken)
    {
        LastError = null;
        try
        {
            #region panel: fork
            var response = await client.Sessions.GetSessionClient(SessionId).ForkAsync(
                new SessionForkRequest { Before = messageId }, cancellationToken: cancellationToken);
            #endregion
            return response.Fork;
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not fork session {SessionId}.", SessionId);
            LastError = "Request failed";
            Changed?.Invoke(this, EventArgs.Empty);
            return null;
        }
    }

    public async Task StopAsync(CancellationToken cancellationToken)
    {
        #region panel: stop
        try
        {
            _ = await client.Sessions.GetSessionClient(SessionId).InterruptAsync(
                new SessionInterruptRequest { Resume = true }, cancellationToken: cancellationToken);
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not stop session {SessionId}.", SessionId);
        }
        #endregion
    }

    /// <summary>Subscribes before loading history, then follows the session until cancelled.</summary>
    public async Task FollowAsync(string sessionId, CancellationToken cancellationToken)
    {
        if (SessionId != sessionId)
        {
            _lines.Clear();
            _live.Clear();
            _outbox.Clear();
            _executing = false;
            EndedTurns = 0;
            LastError = null;
            Ready = false;
            _choicesFolder = null;
        }
        SessionId = sessionId;
        while (true)
        {
            try
            {
                #region panel: follow
                await foreach (var item in client.Events.SubscribeAsync(cancellationToken: cancellationToken))
                {
                    if (item.Type == "server.connected")
                    {
                        // The snapshot carries no delta cursor. A buffered delta can repeat text after refresh until the part's end replaces it.
                        await LoadAsync(cancellationToken);
                    }
                    else if (CatalogFolder(item) is { } folder)
                    {
                        // As upstream's app does, the choices are read again when the server announces its catalog: it
                        // lists a folder's agents empty until it has loaded that folder, then sends these events.
                        if (string.Equals(folder, _choicesFolder, StringComparison.Ordinal))
                        {
                            await LoadChoicesAsync(cancellationToken);
                        }

                        continue;
                    }
                    else if (item is not ISessionScopedEvent scoped || scoped.SessionId != sessionId || !Apply(item))
                    {
                        continue;
                    }

                    Changed?.Invoke(this, EventArgs.Empty);
                }
                #endregion
                return;
            }
            catch (OpenCodeEventSubscriberOverflowException exception)
            {
                logger.LogWarning(exception, "The conversation fell behind; admitting a new subscription before reloading session {SessionId}.", sessionId);
                Ready = false;
                Changed?.Invoke(this, EventArgs.Empty);
            }
        }
    }

    /// <summary>Shows the prompt at once, then sends it with an id of HQ's own; false when the prompt was not sent.</summary>
    public async Task<bool> SendAsync(string text, CancellationToken cancellationToken, string? agent = null, ModelRef? model = null)
    {
        LastError = null;
        #region panel: prompt
        // Like upstream's composer, Send counts as running; a send that sets this takes it back if the server didn't take the prompt.
        var optimisticBusy = !_executing;
        if (optimisticBusy)
        {
            _executing = true;
        }
        // The server takes a first prompt only after loading the session's location, which can take seconds, so the
        // line shows at once under the id HQ mints; the call, the echo or a stored copy confirms it.
        var promptId = $"msg_{Guid.NewGuid():N}";
        _lines.Add(new ConversationLine("you", text, promptId, Pending: true));
        _ = _outbox.Add(promptId);
        _sending++;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
            if (agent is not null || model is not null)
            {
                await SwitchAsync(agent, model, cancellationToken);
            }

            _ = await client.Sessions.GetSessionClient(SessionId).PromptAsync(
                new SessionPromptRequest { Id = promptId, Text = text }, cancellationToken: cancellationToken);
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not send a prompt to session {SessionId}.", SessionId);
            if (_lines.Exists(line => line.PromptId == promptId && !line.Pending))
            {
                // The server's echo or its stored copy shows it took the prompt before the call failed: nothing is undone.
                return true;
            }

            LastError = $"The prompt was not sent: {exception.Message}";
            if (optimisticBusy)
            {
                _executing = false;
            }
            _ = TakeBack(promptId);
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
        }
        catch (OperationCanceledException)
        {
            if (optimisticBusy && !_lines.Exists(line => line.PromptId == promptId && !line.Pending))
            {
                _executing = false;
            }

            throw;
        }
        finally
        {
            _sending--;
        }
        #endregion

        _ = Confirm(promptId);
        Changed?.Invoke(this, EventArgs.Empty);
        return true;
    }

    private static string? CatalogFolder(IEvent item) => item switch
    {
        AgentUpdated agent => agent.Location?.Directory,
        ModelUpdated model => model.Location?.Directory,
        ProviderUpdated provider => provider.Location?.Directory,
        _ => null,
    };

    private bool Apply(IEvent item)
    {
        switch (item)
        {
            case SessionInboxCancelled cancelled:
                return TakeBack(cancelled.Data.InboxId);
            case SessionInboxEnqueued enqueued:
                if (Confirm(enqueued.Data.InboxId))
                {
                    return true;
                }

                if (enqueued.Data.Item is not SessionInboxItemUser user)
                {
                    return false;
                }

                _lines.Add(new ConversationLine("you", user.Payload.Text, enqueued.Data.InboxId));
                _ = _outbox.Add(enqueued.Data.InboxId);
                return true;
            case SessionTextDelta { Data: var delta }:
                Append(delta.AssistantMessageId, delta.Ordinal, delta.Delta, replace: false);
                return true;
            case SessionTextEnded { Data: var ended }:
                Append(ended.AssistantMessageId, ended.Ordinal, ended.Text, replace: true);
                return true;
            case SessionExecutionStarted:
                _executing = true;
                return true;
            case SessionExecutionSucceeded or SessionExecutionFailed or SessionExecutionInterrupted:
                _executing = false;
                EndedTurns++;
                if (item is SessionExecutionFailed failed)
                {
                    LastError = $"The turn failed: {failed.Data.Error.Message}";
                }

                return true;
            default:
                return false;
        }
    }

    private bool TakeBack(string promptId)
    {
        _ = _outbox.Remove(promptId);
        var index = _lines.FindIndex(line => line.PromptId == promptId);
        if (index >= 0)
        {
            // Later live lines move up by one, as upstream reindexes after it takes a row back.
            _lines.RemoveAt(index);
            foreach (var (key, at) in _live.Where(entry => entry.Value > index).ToList())
            {
                _live[key] = at - 1;
            }
        }

        return index >= 0;
    }

    /// <summary>Marks the line of a prompt HQ sent as taken by the server; the line is found by its id.</summary>
    private bool Confirm(string promptId)
    {
        var index = _lines.FindIndex(line => line.PromptId == promptId);
        if (index < 0)
        {
            return false;
        }

        _lines[index] = _lines[index] with { Pending = false };
        return true;
    }

    private void Append(string assistantMessageId, long ordinal, string text, bool replace)
    {
        if (!_live.TryGetValue((assistantMessageId, ordinal), out var index))
        {
            _live[(assistantMessageId, ordinal)] = _lines.Count;
            _lines.Add(new ConversationLine("assistant", text));
            return;
        }

        _lines[index] = _lines[index] with { Text = replace ? text : _lines[index].Text + text };
    }

    private async Task LoadAsync(CancellationToken cancellationToken)
    {
        Ready = false;
        var stored = new List<ConversationLine>();
        var live = new Dictionary<(string AssistantMessageId, long Ordinal), int>();
        await foreach (var message in client.Sessions.GetSessionClient(SessionId).EnumerateMessagesAsync(
            new SessionMessageListRequest { Order = ListOrder.Ascending }, cancellationToken: cancellationToken))
        {
            switch (message)
            {
                case SessionMessageUser user:
                    stored.Add(new ConversationLine("you", user.Text, user.Id));
                    if (_outbox.Remove(user.Id))
                    {
                        // Stored means taken, even if the call that sent it fails before this reload ends.
                        _ = Confirm(user.Id);
                    }

                    break;
                case SessionMessageAssistant assistant:
                    long ordinal = 0;
                    foreach (var text in assistant.Content.OfType<SessionMessageAssistantText>())
                    {
                        live[(assistant.Id, ordinal++)] = stored.Count;
                        stored.Add(new ConversationLine("assistant", text.Text));
                    }

                    break;
            }
        }

        var active = await client.Sessions.GetActiveAsync(cancellationToken: cancellationToken);
        cancellationToken.ThrowIfCancellationRequested();
        // Merged only after every await, because a send can finish meanwhile: a prompt HQ sent keeps its line, in its
        // current state, until the stored history holds it.
        var sent = _lines.FindAll(line => line.PromptId is { } id && _outbox.Contains(id));
        _lines.Clear();
        _lines.AddRange(stored);
        _lines.AddRange(sent);
        _live.Clear();
        foreach (var (key, index) in live)
        {
            _live[key] = index;
        }

        _executing = active.Active.ContainsKey(SessionId);
        Ready = true;
    }
}
