using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Conversation;

/// <summary>One session's stored conversation and live turn.</summary>
internal sealed class SessionConversation(OpenCodeClient client, ILogger<SessionConversation> logger)
{
    private readonly List<ConversationLine> _lines = [];
    private readonly Dictionary<(string AssistantMessageId, long Ordinal), int> _live = [];
    private readonly HashSet<string> _outbox = [];
    private bool _executing;
    private int _sending;

    public event EventHandler? Changed;
    public string SessionId { get; private set; } = "";
    public IReadOnlyList<ConversationLine> Lines => _lines;
    /// <summary>Gets whether the server runs a turn or a prompt is on its way; each owns its own part.</summary>
    public bool Running => _executing || _sending > 0;
    public bool Ready { get; private set; }
    public int EndedTurns { get; private set; }
    public string? LastError { get; private set; }

    /// <summary>Subscribes before loading history, then follows the session until cancelled.</summary>
    public async Task FollowAsync(string sessionId, CancellationToken cancellationToken)
    {
        SessionId = sessionId;
        #region panel: follow
        await foreach (var item in client.Events.SubscribeAsync(cancellationToken: cancellationToken))
        {
            if (item is EventServerConnected)
            {
                // A repeated connection means events were lost; stored state fills that gap.
                await LoadAsync(cancellationToken);
            }
            else if (item is not ISessionScopedEvent scoped || scoped.SessionId != sessionId || !Apply(item))
            {
                continue;
            }

            Changed?.Invoke(this, EventArgs.Empty);
        }
        #endregion
    }

    /// <summary>Shows the prompt at once, then sends it with an id of HQ's own; false when the prompt was not sent.</summary>
    public async Task<bool> SendAsync(string text, CancellationToken cancellationToken)
    {
        LastError = null;
        #region panel: prompt
        // The server takes a first prompt only after loading the session's location, which can take seconds, so the
        // line shows at once under the id HQ mints; the call, the echo or a stored copy confirms it.
        var promptId = $"msg_{Guid.NewGuid():N}";
        _lines.Add(new ConversationLine("you", text, promptId, Pending: true));
        _ = _outbox.Add(promptId);
        _sending++;
        Changed?.Invoke(this, EventArgs.Empty);
        try
        {
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
            Changed?.Invoke(this, EventArgs.Empty);
            return false;
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

    private bool Apply(IEvent item)
    {
        switch (item)
        {
            case SessionInboxEnqueued enqueued:
                return Confirm(enqueued.Data.InboxId);
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
