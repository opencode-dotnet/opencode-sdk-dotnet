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
    public ConversationAgents AgentCalls { get; } = new();
    public ConversationModels ModelCalls { get; } = new();
    public ConversationProviders ProviderCalls { get; } = new();
    public override AgentsClient Agents => AgentCalls;
    public override LanguageModelsClient LanguageModels => ModelCalls;
    public override ProvidersClient Providers => ProviderCalls;
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

    /// <summary>The server's announcement that a folder's agents, models or providers can be listed again.</summary>
    public static IEvent Catalog(string type, string directory)
    {
        var location = new LocationRef { Directory = directory };
        var data = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        return type switch
        {
            "agent.updated" => new AgentUpdated { Id = "evt_agent", Created = 1, Location = location, Data = data },
            "model.updated" => new ModelUpdated { Id = "evt_model", Created = 1, Location = location, Data = data },
            _ => new ProviderUpdated { Id = "evt_provider", Created = 1, Location = location, Data = data },
        };
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
    public static SessionMessageUser User(string id = "msg_1", string text = "question") => new() { Id = id, Text = text, Time = new() { Created = 1 } };
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
    public static SessionInboxEnqueued Enqueued(string inboxId, string sessionId = SessionId) => new()
    {
        Id = "enqueued",
        Created = 4,
        Durable = new() { AggregateId = sessionId, Seq = 3, Version = 1 },
        Data = new()
        {
            SessionId = sessionId,
            InboxId = inboxId,
            Item = new SessionInboxItemUser { Payload = new() { Text = "prompt" }, Delivery = SessionInboxDelivery.Queue },
        },
    };
    public static SessionExecutionStarted Started(string sessionId = SessionId) => new()
    {
        Id = "started",
        Created = 3,
        Durable = new() { AggregateId = sessionId, Seq = 2, Version = 1 },
        Data = new() { SessionId = sessionId },
    };
    public static SessionInboxCancelled Cancelled(string inboxId, string sessionId = SessionId) => new()
    {
        Id = "cancelled",
        Created = 5,
        Durable = new() { AggregateId = sessionId, Seq = 4, Version = 1 },
        Data = new() { SessionId = sessionId, InboxId = inboxId },
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

    public static AgentInfo Agent(string id, AgentInfoMode mode, bool hidden = false) => new()
    {
        // As the server sends them: an id the session addresses, and a display name that differs from it.
        Id = id,
        Name = string.Concat(id[..1].ToUpperInvariant(), id[1..]),
        Mode = mode,
        Hidden = hidden,
        Request = new() { Settings = new(), Headers = new Dictionary<string, string>(StringComparer.Ordinal), Body = new Dictionary<string, JsonElement>(StringComparer.Ordinal) },
        Permissions = [],
    };
    public static ModelInfo Model(string id, string provider = "provider") => new()
    {
        Id = id,
        ModelId = id,
        ProviderId = provider,
        Name = id,
        Capabilities = new() { Tools = false, Input = ["text", "image"], Output = ["text"] },
        Variants = [],
        Time = new() { Released = 1 },
        Cost = [],
        Status = ModelInfoStatus.Active,
        Enabled = true,
        Limit = new() { Context = 1000, Output = 100 },
    };

    internal sealed class ConversationAgents : AgentsClient
    {
        public AgentListRequest? Request { get; private set; }
        public IReadOnlyList<AgentInfo> Items { get; set; } = [];
        /// <summary>Holds the next listing until released; it answers with the list this held, not the current one.</summary>
        public TaskCompletionSource? Hold { get; set; }
        public override async Task<AgentListResponse> ListAgentsAsync(AgentListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Request = request;
            var agents = Items;
            if (Hold is { } hold)
            {
                Hold = null;
                await hold.Task.WaitAsync(cancellationToken);
            }

            return new AgentListResponse { Status = 200, Agents = agents, Location = new() { Directory = "workspace" } };
        }
    }
    internal sealed class ConversationModels : LanguageModelsClient
    {
        public ModelListRequest? Request { get; private set; }
        public ModelDefaultRequest? DefaultRequest { get; private set; }
        public IReadOnlyList<ModelInfo> Items { get; set; } = [];
        public override Task<ModelDefaultResponse> GetDefaultAsync(ModelDefaultRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            DefaultRequest = request;
            return Task.FromResult(new ModelDefaultResponse { Status = 200, Default = Items.Count > 0 ? Items[^1] : null, Location = new() { Directory = "workspace" } });
        }
        public override Task<ModelListResponse> ListModelsAsync(ModelListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new ModelListResponse { Status = 200, Models = Items, Location = new() { Directory = "workspace" } });
        }
    }
    internal sealed class ConversationProviders : ProvidersClient
    {
        public ProviderListRequest? Request { get; private set; }
        public override Task<ProviderListResponse> ListProvidersAsync(ProviderListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Request = request;
            return Task.FromResult(new ProviderListResponse
            {
                Status = 200,
                Location = new() { Directory = "workspace" },
                Providers = [new() { Id = "provider", Name = "Provider name", Package = "package", Activation = ProviderInfoActivation.Enabled }],
            });
        }
    }

    internal sealed class ConversationEvents : EventsClient
    {
        public Queue<IReadOnlyList<Func<IEvent>>> Subscriptions { get; } = new();
        public override EventConnectionState GetConnectionState() => new(EventConnectionStatus.Connected, 0, null);
        public override IAsyncEnumerable<EventConnectionState> WatchConnectionAsync(CancellationToken cancellationToken = default) =>
            new[] { GetConnectionState() }.ToAsyncEnumerable();
        public IReadOnlyList<Func<IEvent>> Items { get; set; } = [];
        public List<string> Trace { get; } = [];
        public override IAsyncEnumerable<IEvent> SubscribeAsync(EventSubscribeRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            (Subscriptions.TryDequeue(out var scripted) ? scripted : Items).Select(next =>
            {
                var item = next();
                Trace.Add("subscription");
                return item;
            }).ToAsyncEnumerable();
    }

    internal sealed class ConversationSessions(SessionClient session) : SessionsClient
    {
        public string? RequestedSession { get; set; }
        public bool Running { get; set; }
        public TaskCompletionSource ActiveStarted { get; set; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ActiveRelease { get; set; }
        public override SessionClient GetSessionClient(string sessionId)
        {
            RequestedSession = sessionId;
            return session;
        }
        public override async Task<SessionActiveResponse> GetActiveAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            ActiveStarted.TrySetResult();
            if (ActiveRelease is { } release)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            return new() { Status = 200, Active = Running ? new Dictionary<string, SessionActive>(StringComparer.Ordinal) { [SessionId] = new() { Type = "running" } } : [with(StringComparer.Ordinal)] };
        }
    }

    internal sealed class ConversationSession : SessionClient
    {
        private bool _ascending;
        public OpenCodeException? ToolFailure { get; set; }
        public SessionForkRequest? Fork { get; private set; }
        public SessionCompactRequest? Compact { get; private set; }
        public SessionSwitchModelRequest? SwitchModel { get; private set; }
        public SessionSwitchAgentRequest? SwitchAgent { get; private set; }
        public bool FailSwitch { get; set; }
        public SessionInfo Info { get; set; } = new()
        {
            Id = SessionId,
            ProjectId = "project",
            Cost = 0,
            Tokens = new() { Input = 0, Output = 0, Reasoning = 0, Cache = new() { Read = 0, Write = 0 } },
            Time = new() { Created = 1, Updated = 1 },
            Location = new() { Directory = "workspace" },
        };
        public override Task<SessionForkResponse> ForkAsync(SessionForkRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Fork = request;
            Trace.Add("fork");
            return ToolFailure is { } failure ? Task.FromException<SessionForkResponse>(failure)
                : Task.FromResult(new SessionForkResponse { Status = 200, Fork = Info with { Id = "ses_fork" } });
        }

        public override Task<SessionResponse> GetAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionResponse { Status = 200, Session = Info });
        public override Task<SessionSwitchModelResponse> SwitchModelAsync(SessionSwitchModelRequest request,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            SwitchModel = request;
            Trace.Add("switchModel");
            return FailSwitch ? Task.FromException<SessionSwitchModelResponse>(new OpenCodeTransportException("offline"))
                : Task.FromResult(new SessionSwitchModelResponse { Status = 204 });
        }
        public override Task<SessionSwitchAgentResponse> SwitchAgentAsync(SessionSwitchAgentRequest request,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            SwitchAgent = request;
            Trace.Add("switchAgent");
            return FailSwitch ? Task.FromException<SessionSwitchAgentResponse>(new OpenCodeTransportException("offline"))
                : Task.FromResult(new SessionSwitchAgentResponse { Status = 204 });
        }
        public override Task<SessionCompactResponse> CompactAsync(SessionCompactRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Compact = request;
            Trace.Add("compact");
            return ToolFailure is { } failure ? Task.FromException<SessionCompactResponse>(failure)
                : Task.FromResult(new SessionCompactResponse
                {
                    Status = 200,
                    Compact = new() { Id = request?.Id.Value ?? "msg_compact", SessionId = SessionId, Time = new() { Created = 1 }, Payload = new Dictionary<string, JsonElement>(StringComparer.Ordinal), Delivery = SessionInboxDelivery.Steer },
                });
        }

        public ICollection<string> Trace { get; set; } = [];
        public IReadOnlyList<ISessionMessageInfo> Messages { get; set; } = [];
        public bool FailPrompt { get; set; }
        public bool FailInterrupt { get; set; }
        public SessionInterruptRequest? Interrupt { get; private set; }
        public override Task<SessionInterruptResponse> InterruptAsync(SessionInterruptRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Interrupt = request;
            return FailInterrupt
                ? Task.FromException<SessionInterruptResponse>(new OpenCodeApiException("unauthorized", 401, new UnauthorizedError { Message = "unauthorized" }, null))
                : Task.FromResult(new SessionInterruptResponse { Status = 200, Interrupt = new() { Interrupted = true } });
        }
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

        public TaskCompletionSource? PromptRelease { get; set; }
        public override async Task<SessionPromptResponse> PromptAsync(SessionPromptRequest request, OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Prompt = request;
            Trace.Add("prompt");
            if (PromptRelease is { } release)
            {
                await release.Task.WaitAsync(cancellationToken);
            }

            return FailPrompt ? throw new OpenCodeApiException("unauthorized", 401, new UnauthorizedError { Message = "unauthorized" }, null)
                : new SessionPromptResponse
                {
                    Status = 200,
                    Prompt = new() { Id = request.Id.Value ?? "msg_server", SessionId = SessionId, Time = new() { Created = 4 }, Payload = new() { Text = request.Text }, Delivery = SessionInboxDelivery.Queue },
                };
        }
    }
}
