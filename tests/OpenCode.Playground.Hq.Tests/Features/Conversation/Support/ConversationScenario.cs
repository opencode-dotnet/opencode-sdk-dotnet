using System.Globalization;
using System.Text.Json;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCode.Playground.Hq.Features.Conversation;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

namespace OpenCode.Playground.Hq.Tests.Features.Conversation.Support;

internal sealed class ConversationScenario : OpenCodeClient
{
    public const string SessionId = "ses_conversation";
    public const string ErrorMessage = "provider.free-limit: limit reached";
    public ConversationScenario()
    {
        SessionCalls = new ConversationSessions(MessageCalls);
        Conversation = new SessionConversation(this, NullLogger<SessionConversation>.Instance);
        MessageCalls.Trace = EventCalls.Trace;
    }
    public SessionConversation Conversation { get; }
    public ConversationSessions SessionCalls { get; }
    public ConversationSession MessageCalls { get; } = new();
    public ConversationEvents EventCalls { get; } = new();
    public override SessionsClient Sessions => SessionCalls;
    public override EventsClient Events => EventCalls;

    public ConversationScenario WithHistory(params ISessionMessageInfo[] messages)
    {
        MessageCalls.Messages = messages;
        return this;
    }

    public Task FollowAsync(params IEvent[] events)
    {
        EventCalls.Items = [Connected, .. events.Select(item => (Func<IEvent>)(() => item))];
        return Conversation.FollowAsync(SessionId, CancellationToken.None);
    }

    public static EventServerConnected Connected() => new() { Id = "connected", Data = new Dictionary<string, JsonElement>(StringComparer.Ordinal) };
    public static SessionMessageAssistantText Text(string text) => new() { Text = text };
    public static SessionMessageAssistantReasoning Reasoning() => new() { Text = "private reasoning" };
    public static SessionMessageAssistantTool Tool() => new()
    {
        Id = "tool",
        Name = "edit",
        Time = new() { Created = 2 },
        State = new SessionMessageToolStateCompleted { Input = new Dictionary<string, JsonElement>(StringComparer.Ordinal), Content = [] },
    };
    public static SessionMessageUser User() => new() { Id = "msg_1", Text = "question", Time = new() { Created = 1 } };
    public static SessionMessageSystem SystemMessage() => new() { Id = "msg_2", Text = "system", Time = new() { Created = 2 } };
    public static SessionMessageAssistant Assistant(params ISessionMessageAssistantContent[] content) => new()
    {
        Id = "msg_3",
        Time = new() { Created = 3 },
        Agent = "build",
        Model = new() { ProviderId = "provider", Id = "model" },
        Content = content,
    };
    public static SessionTextDelta Delta(long ordinal, string text, string sessionId = SessionId) => new()
    {
        Id = "delta",
        Created = 4,
        Data = new() { SessionId = sessionId, AssistantMessageId = "msg_3", Ordinal = ordinal, Delta = text },
    };
    public static SessionTextEnded Ended(long ordinal, string text, string sessionId = SessionId) => new()
    {
        Id = "ended",
        Created = 5,
        Durable = new() { AggregateId = sessionId, Seq = 1, Version = 1 },
        Data = new() { SessionId = sessionId, AssistantMessageId = "msg_3", Ordinal = ordinal, Text = text },
    };
    public static SessionExecutionStarted Started(string sessionId = SessionId) => new()
    {
        Id = "started",
        Created = 3,
        Durable = new() { AggregateId = sessionId, Seq = 2, Version = 1 },
        Data = new() { SessionId = sessionId },
    };
    public static SessionExecutionSucceeded Succeeded(string sessionId = SessionId) => new()
    {
        Id = "succeeded",
        Created = 6,
        Durable = new() { AggregateId = sessionId, Seq = 3, Version = 1 },
        Data = new() { SessionId = sessionId },
    };
    public static SessionExecutionInterrupted Interrupted() => new()
    {
        Id = "interrupted",
        Created = 6,
        Durable = new() { AggregateId = SessionId, Seq = 4, Version = 1 },
        Data = new() { SessionId = SessionId, Reason = SessionExecutionInterruptedDataReason.User },
    };
    public static SessionExecutionFailed Failed() => new()
    {
        Id = "failed",
        Created = 6,
        Durable = new() { AggregateId = SessionId, Seq = 5, Version = 1 },
        Data = new() { SessionId = SessionId, Error = new() { Type = "provider.free-limit", Message = ErrorMessage } },
    };

    internal sealed class ConversationEvents : EventsClient
    {
        public IReadOnlyList<Func<IEvent>> Items { get; set; } = [];
        public List<string> Trace { get; } = [];
        public override IAsyncEnumerable<IEvent> SubscribeAsync(EventSubscribeRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            Items.Select(next =>
            {
                var item = next();
                Trace.Add("subscription");
                return item;
            }).ToAsyncEnumerable();
    }

    internal sealed class ConversationSessions(SessionClient session) : SessionsClient
    {
        public bool Running { get; set; }
        public TaskCompletionSource ActiveStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ActiveRelease { get; set; }
        public override SessionClient GetSessionClient(string sessionId) => session;
        public override async Task<SessionActiveResponse> GetActiveAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            ActiveStarted.TrySetResult();
            if (ActiveRelease is { } release)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            return new() { Status = 200, Active = Running ? new Dictionary<string, SessionActive>(StringComparer.Ordinal) { [SessionId] = new() { Type = "running" } } : new Dictionary<string, SessionActive>(StringComparer.Ordinal) };
        }
    }

    internal sealed class ConversationSession : SessionClient
    {
        private bool _ascending;
        public ICollection<string> Trace { get; set; } = [];
        public IReadOnlyList<ISessionMessageInfo> Messages { get; set; } = [];
        public bool FailPrompt { get; set; }
        public SessionPromptRequest? Prompt { get; private set; }
        public TaskCompletionSource MessagesStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? MessagesRelease { get; set; }
        public override async Task<SessionMessageListResponse> ListMessagesAsync(SessionMessageListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Trace.Add("history");
            MessagesStarted.TrySetResult();
            if (MessagesRelease is { } release)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            if (request?.Cursor is null)
            {
                _ascending = request?.Order == ListOrder.Ascending;
            }

            var ordered = _ascending ? Messages.OrderBy(static message => message.Id, StringComparer.Ordinal) : Messages.OrderByDescending(static message => message.Id, StringComparer.Ordinal);
            var offset = request?.Cursor is { } cursor ? int.Parse(cursor, CultureInfo.InvariantCulture) : 0;
            return new() { Status = 200, Messages = [.. ordered.Skip(offset).Take(1)], Cursor = new() { Next = offset + 1 < Messages.Count ? (offset + 1).ToString(CultureInfo.InvariantCulture) : null } };
        }

        public override Task<SessionPromptResponse> PromptAsync(SessionPromptRequest request, OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Prompt = request;
            return FailPrompt ? Task.FromException<SessionPromptResponse>(new OpenCodeApiException("unauthorized", 401, new UnauthorizedError { Message = "unauthorized" }, null))
                : Task.FromResult(new SessionPromptResponse
                {
                    Status = 200,
                    Prompt = new() { Id = request.Id.Value ?? "msg_server", SessionId = SessionId, Time = new() { Created = 4 }, Payload = new() { Text = request.Text }, Delivery = SessionInboxDelivery.Queue },
                });
        }
    }
}
