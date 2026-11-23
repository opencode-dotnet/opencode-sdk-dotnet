using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenCode.Playground.Hq.Features.Sessions;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;
using static OpenCode.Playground.Hq.Tests.Features.Sessions.Support.BoardScenario.BoardData;

namespace OpenCode.Playground.Hq.Tests.Features.Sessions.Support;

internal sealed class BoardScenario : OpenCodeClient, IAsyncDisposable
{
    public BoardScenario()
    {
        Board = new SessionBoard(this, Logger);
        SessionCalls.Trace = EventCalls.Trace;
    }
    public SessionBoard Board { get; }
    public BoardLogger Logger { get; } = new();
    public BoardSessions SessionCalls { get; } = new();
    public BoardPermissions PermissionCalls { get; } = new();
    public BoardForms FormCalls { get; } = new();
    public BoardEvents EventCalls { get; } = new();
    public override SessionsClient Sessions => SessionCalls;
    public override PermissionsClient Permissions => PermissionCalls;
    public override FormsClient Forms => FormCalls;
    public override EventsClient Events => EventCalls;

    public async Task<int> AnswerFormAsync(string? failure)
    {
        await StartAsync();
        SessionCalls.FormFailure = failure switch
        {
            null => null,
            nameof(FormNotFoundError) => new OpenCodeApiException("gone", 404, new FormNotFoundError { Id = "form_first", Message = "gone" }, null),
            nameof(FormAlreadySettledError) => new OpenCodeApiException("settled", 409, new FormAlreadySettledError { Id = "form_first", Message = "settled" }, null),
            nameof(UnauthorizedError) => Unauthorized(),
            nameof(OpenCodeTransportException) => new OpenCodeTransportException("connection lost"),
            _ => throw new ArgumentOutOfRangeException(nameof(failure)),
        };
        var changed = 0;
        Board.Changed += (_, _) => changed++;
        await Board.AnswerFormAsync(Form(), Reply(), CancellationToken.None);
        return changed;
    }

    public async Task StartAsync()
    {
        await Board.StartAsync(CancellationToken.None);
        await SettleAsync();
        await SendAsync(Connected());
    }

    public Task AskElsewhereAsync() => SendAsync(new PermissionAsked
    {
        Id = "other",
        Created = 2,
        Data = new() { Id = "ask_other", SessionId = "ses_1", Action = "edit", Resources = ["other"] },
    });

    public async Task SendAsync(IEvent item)
    {
        EventCalls.Input.Writer.TryWrite(() => item);
        await SettleAsync();
    }

    public async Task SendStateAsync(EventConnectionState state)
    {
        while (EventCalls.StatePulls.Reader.TryRead(out _))
        {
            // Drain acknowledgements for the state that preceded this explicit change.
        }

        EventCalls.States.Writer.TryWrite(state);
        await EventCalls.StatePulls.Reader.ReadAsync(CancellationToken.None).AsTask().WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    public async Task SettleAsync()
    {
        // A pull after handling an event, or the service ending, bounds the observation; no sleep decides a verdict.
        await Task.WhenAny(EventCalls.Pulls.Reader.ReadAsync(CancellationToken.None).AsTask(), Board.ExecuteTask ?? Task.CompletedTask)
            .WaitAsync(TimeSpan.FromSeconds(10), CancellationToken.None);
    }

    public async ValueTask DisposeAsync()
    {
        await Board.StopAsync(CancellationToken.None);
        Board.Dispose();
        Dispose();
    }

    internal static class BoardData
    {
        internal static readonly FormStringField Pick = new() { Key = "pick", Options = [new FormOption { Label = "A", Value = "a" }] };

        internal static FormInfo Form() => new() { Id = "form_first", SessionId = "ses_0", Title = "question", Fields = [Pick] };
        internal static SessionFormReplyRequest Reply() => new() { Answer = new Dictionary<string, FormValue>(StringComparer.Ordinal) };
        internal static OpenCodeApiException Unauthorized() => new("unauthorized", 401, new UnauthorizedError { Message = "unauthorized" }, null);
        internal static EventServerConnected Connected() => new() { Id = "connected", Data = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal) };
        internal static SessionInfo Session(int index, string directory) => new()
        {
            Id = "ses_" + index.ToString(CultureInfo.InvariantCulture),
            ProjectId = "project",
            Title = directory,
            Cost = 0,
            Tokens = new TokenUsageInfo { Input = 0, Output = 0, Reasoning = 0, Cache = new TokenUsageInfoCache { Read = 0, Write = 0 } },
            Time = new SessionInfoTime { Created = index, Updated = index },
            Location = new LocationPublicRef { Directory = directory },
        };
    }

    internal sealed class BoardEvents : EventsClient
    {
        private EventConnectionState _state = new(EventConnectionStatus.Connected, 0, null);
        public Exception? WatchFailure { get; set; }
        public List<string> Trace { get; } = [];
        public int SubscribeCalls { get; private set; }
        public int WatchCalls { get; private set; }
        public CancellationToken WatchToken { get; private set; }
        public TaskCompletionSource WatchEnded { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public Channel<EventConnectionState> States { get; } = Channel.CreateUnbounded<EventConnectionState>();
        public Channel<byte> StatePulls { get; } = Channel.CreateUnbounded<byte>();
        public override EventConnectionState GetConnectionState() => _state;
        public override async IAsyncEnumerable<EventConnectionState> WatchConnectionAsync([EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            WatchCalls++;
            WatchToken = cancellationToken;
            try
            {
                yield return _state;
                while (!cancellationToken.IsCancellationRequested)
                {
                    if (WatchFailure is { } failure)
                    {
                        throw failure;
                    }

                    StatePulls.Writer.TryWrite(0);
                    _state = await States.Reader.ReadAsync(cancellationToken);
                    yield return _state;
                }
            }
            finally
            {
                WatchEnded.TrySetResult();
            }
        }

        public Channel<Func<IEvent>> Input { get; } = Channel.CreateUnbounded<Func<IEvent>>();
        public Channel<byte> Pulls { get; } = Channel.CreateUnbounded<byte>();
        public override async IAsyncEnumerable<IEvent> SubscribeAsync(EventSubscribeRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            SubscribeCalls++;
            Trace.Add("subscribe");
            while (!cancellationToken.IsCancellationRequested)
            {
                Pulls.Writer.TryWrite(0);
                var item = (await Input.Reader.ReadAsync(cancellationToken))();
                if (item.Type == "server.connected")
                {
                    Trace.Add("admission");
                }

                yield return item;
            }
        }
    }

    internal sealed class BoardLogger : ILogger<SessionBoard>
    {
        public Exception? LastFailure { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => LastFailure = exception;
    }

    internal sealed class BoardSessions : SessionsClient
    {
        public OpenCodeException? ActionFailure { get; set; }
        public string? UpdatedSession { get; set; }
        public SessionUpdateRequest? UpdateRequest { get; set; }
        public string? RemovedSession { get; set; }
        public List<string> ReadSessions { get; } = [];
        public SessionListRequest? ListRequest { get; private set; }
        public List<string> Trace { get; set; } = [];
        public int Refreshes { get; private set; }
        private bool _descending;
        public IReadOnlyList<SessionInfo> Items { get; set; } = [Session(0, "first"), Session(1, "gone"), Session(2, "last")];
        public IReadOnlySet<string> Active { get; set; } = new HashSet<string>(["ses_0", "ses_1", "ses_2"], StringComparer.Ordinal);
        public OpenCodeApiException? ReplyFailure { get; set; }
        public OpenCodeException? FormFailure { get; set; }
        public string? CancelSession { get; set; }
        public string? CancelId { get; set; }
        public string? PermissionSession { get; set; }
        public string? PermissionId { get; set; }
        public PermissionReply? Decision { get; set; }
        public IReadOnlyList<string> Pending { get; set; } = [];
        public List<string> Relisted { get; } = [];
        public TaskCompletionSource ListingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ListingRelease { get; set; }
        public override SessionClient GetSessionClient(string sessionId) => new BoardSession(this, sessionId);
        public override Task<SessionActiveResponse> GetActiveAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionActiveResponse { Status = 200, Active = Active.ToDictionary(static id => id, static _ => new SessionActive { Type = "running" }, StringComparer.Ordinal) });
        public override async Task<SessionListResponse> ListSessionsAsync(SessionListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            ListRequest = request;
            if (request?.Cursor is null)
            {
                _descending = request?.Order == ListOrder.Descending;
                Refreshes++;
                Trace.Add("refresh");
            }

            if (ListingRelease is { } release)
            {
                ListingStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            var ordered = _descending ? Items.OrderByDescending(static item => item.Time.Updated) : Items.OrderBy(static item => item.Time.Updated);
            var offset = request?.Cursor is { } cursor ? int.Parse(cursor, CultureInfo.InvariantCulture) : 0;
            var page = ordered.Skip(offset).Take(200).ToArray();
            return new SessionListResponse
            {
                Status = 200,
                Sessions = page,
                Cursor = new ListCursor
                {
                    Next = offset + page.Length < Items.Count ? (offset + page.Length).ToString(CultureInfo.InvariantCulture) : null,
                }
            };
        }
    }

    internal sealed class BoardSession(BoardSessions owner, string sessionId) : SessionClient
    {
        public override Task<SessionRemoveResponse> RemoveAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            owner.RemovedSession = sessionId;
            return owner.ActionFailure is { } failure ? Task.FromException<SessionRemoveResponse>(failure)
                : Task.FromResult(new SessionRemoveResponse { Status = 204 });
        }

        public override Task<SessionUpdateResponse> UpdateAsync(SessionUpdateRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            owner.UpdatedSession = sessionId;
            owner.UpdateRequest = request;
            return owner.ActionFailure is { } failure ? Task.FromException<SessionUpdateResponse>(failure)
                : Task.FromResult(new SessionUpdateResponse { Status = 204 });
        }

        public override Task<SessionFormCancelResponse> CancelFormAsync(string formId, SessionFormCancelRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            owner.CancelSession = sessionId;
            owner.CancelId = formId;
            return owner.FormFailure is { } failure ? Task.FromException<SessionFormCancelResponse>(failure)
                : Task.FromResult(new SessionFormCancelResponse { Status = 204 });
        }

        public override Task<SessionResponse> GetAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            owner.ReadSessions.Add(sessionId);
            return Task.FromResult(new SessionResponse { Status = 200, Session = owner.Items.Single(item => item.Id == sessionId) });
        }

        public override Task<SessionPermissionListResponse> ListRequestsAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            owner.Relisted.Add(sessionId);
            return Task.FromResult(new SessionPermissionListResponse
            {
                Status = 200,
                Requests = [.. owner.Pending.Select(id => new PermissionRequest { Id = id, SessionId = sessionId, Action = "edit", Resources = ["file"] })],
            });
        }

        public override Task<SessionFormListResponse> ListFormsAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionFormListResponse { Status = 200, Forms = [] });

        public override Task<SessionFormReplyResponse> ReplyToFormAsync(string formId, SessionFormReplyRequest request,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return owner.FormFailure is { } failure ? Task.FromException<SessionFormReplyResponse>(failure)
                : Task.FromResult(new SessionFormReplyResponse { Status = 204 });
        }

        public override Task<SessionPermissionReplyResponse> ReplyToPermissionAsync(string requestId, SessionPermissionReplyRequest request,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            owner.PermissionSession = sessionId;
            owner.PermissionId = requestId;
            owner.Decision = request.Decision;
            return owner.ReplyFailure is { } failure ? Task.FromException<SessionPermissionReplyResponse>(failure)
                : Task.FromResult(new SessionPermissionReplyResponse { Status = 204 });
        }
    }

    internal sealed class BoardPermissions : PermissionsClient
    {
        public List<string> Directories { get; } = [];
        public string? FailedDirectory { get; set; }
        public override Task<PermissionRequestListResponse> ListRequestsAsync(PermissionRequestListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            var directory = request?.Location?.Directory ?? string.Empty;
            Directories.Add(directory);
            return directory == FailedDirectory ? Task.FromException<PermissionRequestListResponse>(Unauthorized())
                : Task.FromResult(new PermissionRequestListResponse
                {
                    Status = 200,
                    Location = new LocationPublicRef { Directory = directory },
                    Requests = [new PermissionRequest
                {
                    Id = "ask_" + directory, SessionId = "ses_0", Action = "edit", Resources = ["file"],
                }]
                });
        }
    }

    internal sealed class BoardForms : FormsClient
    {
        public List<string> Directories { get; } = [];
        public override Task<FormListResponse> ListFormsAsync(FormListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            Directories.Add(request?.Location?.Directory ?? string.Empty);
            return Task.FromResult(new FormListResponse
            {
                Status = 200,
                Location = new LocationPublicRef { Directory = request?.Location?.Directory ?? string.Empty },
                Forms = [new FormInfo
            {
                Id = "form_" + request?.Location?.Directory, SessionId = "ses_0", Title = "question", Fields = [Pick],
            }]
            });
        }
    }
}
