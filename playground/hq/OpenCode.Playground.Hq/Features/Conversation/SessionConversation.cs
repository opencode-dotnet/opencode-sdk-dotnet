using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Features.Conversation;

/// <summary>One session's stored conversation and live turn.</summary>
internal sealed class SessionConversation(OpenCodeClient client, ILogger<SessionConversation> logger)
{
    private readonly List<ConversationLine> _lines = [];
    private readonly Dictionary<(string AssistantMessageId, long Ordinal), int> _live = [];

    public event EventHandler? Changed;
    public string SessionId { get; private set; } = "";
    public IReadOnlyList<ConversationLine> Lines => _lines;
    public bool Running { get; private set; }
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

    /// <summary>Sends a prompt with an id of HQ's own.</summary>
    public async Task SendAsync(string text, CancellationToken cancellationToken)
    {
        LastError = null;
        try
        {
            #region panel: prompt
            _ = await client.Sessions.GetSessionClient(SessionId).PromptAsync(
                new SessionPromptRequest { Id = $"msg_{Guid.NewGuid():N}", Text = text }, cancellationToken: cancellationToken);
            #endregion
            _lines.Add(new ConversationLine("you", text));
        }
        catch (OpenCodeException exception)
        {
            logger.LogWarning(exception, "Could not send a prompt to session {SessionId}.", SessionId);
            LastError = $"The prompt was not sent: {exception.Message}";
        }

        Changed?.Invoke(this, EventArgs.Empty);
    }

    private bool Apply(IEvent item)
    {
        switch (item)
        {
            case SessionTextDelta { Data: var delta }:
                Append(delta.AssistantMessageId, delta.Ordinal, delta.Delta, replace: false);
                return true;
            case SessionTextEnded { Data: var ended }:
                Append(ended.AssistantMessageId, ended.Ordinal, ended.Text, replace: true);
                return true;
            case SessionExecutionStarted:
                Running = true;
                return true;
            case SessionExecutionSucceeded or SessionExecutionFailed or SessionExecutionInterrupted:
                Running = false;
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
        _lines.Clear();
        _live.Clear();
        await foreach (var message in client.Sessions.GetSessionClient(SessionId).EnumerateMessagesAsync(
            new SessionMessageListRequest { Order = ListOrder.Ascending }, cancellationToken: cancellationToken))
        {
            switch (message)
            {
                case SessionMessageUser user:
                    _lines.Add(new ConversationLine("you", user.Text));
                    break;
                case SessionMessageAssistant assistant:
                    long ordinal = 0;
                    foreach (var text in assistant.Content.OfType<SessionMessageAssistantText>())
                    {
                        Append(assistant.Id, ordinal++, text.Text, replace: true);
                    }

                    break;
            }
        }

        var active = await client.Sessions.GetActiveAsync(cancellationToken: cancellationToken);
        Running = active.Active.ContainsKey(SessionId);
        Ready = true;
    }
}
