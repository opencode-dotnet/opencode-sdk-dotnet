using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging;
using OpenCode.Playground.Hq.Features.Sessions;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Playground.Hq.Tests.Features.Sessions;

public sealed class SessionBoardTests
{
    [Test]
    public async Task SessionBoard_Should_Apply_A_Title_After_An_Untitled_Session_Is_Created()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        scenario.SessionCalls.Items = [Session(9, "new") with { Title = null }];
        await scenario.SendAsync(new SessionCreated
        {
            Id = "created",
            Created = 1,
            Durable = new() { AggregateId = "ses_9", Seq = 1, Version = 1 },
            Data = new() { SessionId = "ses_9", ProjectId = "project", Location = new() { Directory = "new" }, Slug = "new", Version = "2.3.2" },
        });
        await Assert.That(scenario.Board.Sessions.Single(static row => row.Session.Id == "ses_9").Session.Title).IsNull();
        await scenario.SendAsync(new SessionRenamed
        {
            Id = "renamed",
            Created = 2,
            Durable = new() { AggregateId = "ses_9", Seq = 2, Version = 1 },
            Data = new() { SessionId = "ses_9", Title = "Later title" },
        });

        await Assert.That(scenario.Board.Sessions.Single(static row => row.Session.Id == "ses_9").Session.Title).IsEqualTo("Later title");
    }

    [Test]
    public async Task SessionBoard_Should_Report_A_Watch_Fault_Before_Shutdown()
    {
        await using var scenario = new BoardScenario();
        var failure = new InvalidOperationException("watch lost");
        scenario.EventCalls.WatchFailure = failure;
        await scenario.StartAsync();

        await Assert.That(scenario.Board.Connected).IsFalse();
        await Assert.That(scenario.Board.LastError).IsEqualTo("watch lost");
        await Assert.That(scenario.Logger.LastFailure).IsSameReferenceAs(failure);
        await Assert.That(scenario.EventCalls.SubscribeCalls).IsEqualTo(1);
    }

    [Test]
    public async Task SessionBoard_Should_Keep_A_Permission_Answer_Error_After_Refresh()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        scenario.SessionCalls.ReplyFailure = Unauthorized();
        var ask = scenario.Board.NeedsYou.Single(static item => item.Id == "ask_first");
        await scenario.Board.AnswerAsync(ask, PermissionReply.Once, CancellationToken.None);
        scenario.SessionCalls.Items = [Session(7, "refreshed")];
        await scenario.SendAsync(Connected());

        await Assert.That(scenario.Board.LastError).IsEqualTo("Could not answer permission. Try again.");
        await Assert.That(scenario.Board.Sessions.Single().Session.Id).IsEqualTo("ses_7");
    }

    [Test]
    public async Task SessionBoard_Should_Own_And_Observe_The_State_Watch_With_Its_Hosting_Lifetime()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        await Assert.That(scenario.EventCalls.WatchCalls).IsEqualTo(1);
        await Assert.That(scenario.Board.Connected).IsTrue();
        var failure = Unauthorized();
        await scenario.SendStateAsync(new EventConnectionState(EventConnectionStatus.Reconnecting, 1, failure));
        await Assert.That(scenario.Board.Connected).IsFalse();
        await Assert.That(scenario.Board.LastError).IsEqualTo(failure.Message);
        await Assert.That(scenario.EventCalls.SubscribeCalls).IsEqualTo(1);
        await scenario.Board.StopAsync(CancellationToken.None);
        await Assert.That(scenario.EventCalls.WatchToken.IsCancellationRequested).IsTrue();
        await Assert.That(scenario.EventCalls.WatchEnded.Task.Status).IsEqualTo(TaskStatus.RanToCompletion);
    }

    [Test]
    public async Task SessionBoard_Should_Recover_From_Overflow_By_Admission_Then_Refresh_Then_An_Idempotent_Tail()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        var refreshes = scenario.SessionCalls.Refreshes;
        scenario.EventCalls.Trace.Clear();
        scenario.SessionCalls.Items = [Session(9, "last")];
        scenario.SessionCalls.Active = new HashSet<string>(["ses_9"], StringComparer.Ordinal);
        scenario.SessionCalls.ListingRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.EventCalls.Input.Writer.TryWrite(() =>
        {
            scenario.EventCalls.Trace.Add("overflow");
            throw new OpenCodeEventSubscriberOverflowException();
        });
        await scenario.SettleAsync();
        await Assert.That(scenario.EventCalls.SubscribeCalls).IsEqualTo(2);
        await Assert.That(scenario.SessionCalls.Refreshes).IsEqualTo(refreshes);
        await Assert.That(scenario.EventCalls.Trace).IsEquivalentTo(["overflow", "subscribe"], CollectionOrdering.Matching);
        var refreshing = scenario.SendAsync(Connected());
        try
        {
            await scenario.SessionCalls.ListingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
            await Assert.That(scenario.EventCalls.Trace)
                .IsEquivalentTo(["overflow", "subscribe", "admission", "refresh"], CollectionOrdering.Matching);
            var started = new SessionExecutionStarted
            {
                Id = "started",
                Created = 1,
                Durable = new() { AggregateId = "ses_9", Seq = 1, Version = 1 },
                Data = new() { SessionId = "ses_9" },
            };
            scenario.EventCalls.Input.Writer.TryWrite(() => started);
        }
        finally
        {
            scenario.SessionCalls.ListingRelease.TrySetResult();
            await refreshing;
        }

        await scenario.SettleAsync();
        await Assert.That(scenario.Board.Sessions.Select(static row => row.Session.Id)).IsEquivalentTo(["ses_9"], CollectionOrdering.Matching);
        await Assert.That(scenario.Board.Sessions.Single().Status).IsEqualTo("running");
    }

    [Test]
    public async Task SessionBoard_Should_Refresh_On_A_Drifted_Connection_Marker()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        scenario.SessionCalls.Items = [Session(9, "last")];
        var payload = System.Text.Json.JsonSerializer.SerializeToElement(new { type = "server.connected", data = false });
        await scenario.SendAsync(new UnknownEvent("server.connected", payload));

        await Assert.That(scenario.Board.Sessions.Select(static row => row.Session.Id)).IsEquivalentTo(["ses_9"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SessionBoard_Should_Not_List_Asks_For_Directories_Without_An_Active_Session()
    {
        await using var scenario = new BoardScenario();
        scenario.SessionCalls.Active = new HashSet<string>(["ses_0", "ses_2"], StringComparer.Ordinal);
        await scenario.StartAsync();
        await Assert.That(scenario.PermissionCalls.Directories).IsEquivalentTo(["last", "first"], CollectionOrdering.Matching);
        await Assert.That(scenario.FormCalls.Directories).IsEquivalentTo(["last", "first"], CollectionOrdering.Matching);
        await Assert.That(scenario.Board.Sessions.Select(static row => row.Session.Id)).IsEquivalentTo(["ses_2", "ses_1", "ses_0"], CollectionOrdering.Matching);
        await Assert.That(scenario.Board.NeedsYou.Select(static item => item.Id))
            .IsEquivalentTo(["ask_first", "ask_last", "form_first", "form_last"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SessionBoard_Should_Keep_Other_Directories_When_One_List_Fails()
    {
        await using var scenario = new BoardScenario();
        scenario.PermissionCalls.FailedDirectory = "gone";
        await scenario.StartAsync();
        await Assert.That(scenario.Board.Sessions.Select(static row => row.Session.Id)).IsEquivalentTo(["ses_2", "ses_1", "ses_0"], CollectionOrdering.Matching);
        await Assert.That(scenario.Board.NeedsYou.Select(static item => item.Id).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["ask_first", "ask_last", "form_first", "form_last"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(true)]
    [Arguments(false)]
    public async Task AnswerAsync_Should_Remove_Only_An_Already_Answered_Ask(bool notFound)
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        scenario.SessionCalls.ReplyFailure = notFound
            ? new OpenCodeApiException("answered", 404, new PermissionNotFoundError { RequestId = "ask_first", Message = "answered" }, null)
            : Unauthorized();
        var item = scenario.Board.NeedsYou.Single(static item => item.Id == "ask_first");
        var changed = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        scenario.Board.Changed += (_, _) => changed.TrySetResult();
        await Assert.That(() => scenario.Board.AnswerAsync(item, PermissionReply.Once, CancellationToken.None)).ThrowsNothing();
        await Assert.That(changed.Task.IsCompleted).IsTrue();
        var expected = notFound ? new[] { "ask_gone", "ask_last", "form_first", "form_gone", "form_last" }
            : ["ask_first", "ask_gone", "ask_last", "form_first", "form_gone", "form_last"];
        await Assert.That(scenario.Board.NeedsYou.Select(static ask => ask.Id).Order(StringComparer.Ordinal)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        if (!notFound)
        {
            await Assert.That(scenario.Board.LastError).IsEqualTo("Could not answer permission. Try again.");
        }
    }

    [Test]
    public async Task SessionBoard_Should_Not_Be_Connected_While_Reseeding()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        scenario.SessionCalls.Items = [Session(9, "last")];
        scenario.SessionCalls.ListingRelease = new(TaskCreationOptions.RunContinuationsAsynchronously);
        var changed = false;
        scenario.Board.Changed += (_, _) => changed = true;
        var refresh = scenario.SendAsync(Connected());
        await scenario.SessionCalls.ListingStarted.Task.WaitAsync(TimeSpan.FromSeconds(10));
        await Assert.That(scenario.Board.Connected).IsFalse();
        await Assert.That(changed).IsTrue();
        scenario.SessionCalls.ListingRelease.SetResult();
        await refresh;
        await Assert.That(scenario.Board.Connected).IsTrue();
        await Assert.That(scenario.Board.Sessions.Select(static row => row.Session.Id)).IsEquivalentTo(["ses_9"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SessionBoard_Should_Refresh_Lists_On_Repeated_Connected()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        scenario.SessionCalls.Items = [Session(9, "last")];
        scenario.SessionCalls.Active = new HashSet<string>(["ses_9"], StringComparer.Ordinal);
        await scenario.SendAsync(Connected());
        await Assert.That(scenario.Board.Sessions.Select(static row => row.Session.Id)).IsEquivalentTo(["ses_9"], CollectionOrdering.Matching);
        await Assert.That(scenario.Board.NeedsYou.Select(static item => item.Id).Order(StringComparer.Ordinal))
            .IsEquivalentTo(["ask_last", "form_last"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SessionBoard_Should_Disconnect_And_Recover_After_Stream_Api_Failure()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        await scenario.SendStateAsync(new EventConnectionState(EventConnectionStatus.Reconnecting, 1, Unauthorized()));
        await Assert.That(scenario.Board.Connected).IsFalse();
        scenario.SessionCalls.Items = [Session(8, "first")];
        scenario.SessionCalls.Active = new HashSet<string>(["ses_8"], StringComparer.Ordinal);
        await scenario.SendStateAsync(new EventConnectionState(EventConnectionStatus.Connected, 0, null));
        await scenario.SendAsync(Connected());
        await Assert.That(scenario.Board.Connected).IsTrue();
        await Assert.That(scenario.Board.Sessions.Select(static row => row.Session.Id)).IsEquivalentTo(["ses_8"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task SessionBoard_Should_Seed_Exactly_The_Newest_500()
    {
        await using var scenario = new BoardScenario();
        scenario.SessionCalls.Items = [.. Enumerable.Range(0, 503).Select(static index => Session(index, "first"))];
        scenario.SessionCalls.Active = scenario.SessionCalls.Items.Select(static item => item.Id).ToHashSet(StringComparer.Ordinal);
        await scenario.StartAsync();
        var expected = Enumerable.Range(3, 500).Reverse().Select(static index => "ses_" + index.ToString(CultureInfo.InvariantCulture));
        await Assert.That(scenario.Board.Sessions.Select(static row => row.Session.Id)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    public async Task NeedsYou_Should_Keep_The_Seeded_Form()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();

        var form = scenario.Board.NeedsYou.Single(static item => item.Id == "form_first").Form;
        await Assert.That(form?.Fields.Single()).IsSameReferenceAs(Pick);
    }

    [Test]
    [Arguments(null, true, null)]
    [Arguments(nameof(FormNotFoundError), true, null)]
    [Arguments(nameof(FormAlreadySettledError), true, null)]
    [Arguments(nameof(UnauthorizedError), false, "Could not answer the question: unauthorized")]
    [Arguments(nameof(OpenCodeTransportException), false, "Could not answer the question: connection lost")]
    public async Task AnswerFormAsync_Should_Apply_The_Expected_Outcome_And_Raise_Changed(string? failure, bool removed, string? error)
    {
        await using var scenario = new BoardScenario();
        var changed = await scenario.AnswerFormAsync(failure);
        var expected = removed ? new[] { "ask_first", "ask_gone", "ask_last", "form_gone", "form_last" }
            : ["ask_first", "ask_gone", "ask_last", "form_first", "form_gone", "form_last"];

        await Assert.That(scenario.Board.NeedsYou.Select(static item => item.Id)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(scenario.Board.LastError).IsEqualTo(error);
        await Assert.That(changed).IsEqualTo(1);
    }

    [Test]
    public async Task AnswerFormAsync_Should_Propagate_Cancellation_Without_Changing_The_Board()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();
        var changed = 0;
        scenario.Board.Changed += (_, _) => changed++;
        await Assert.That(() => scenario.Board.AnswerFormAsync(Form(), Reply(), cancellation.Token)).Throws<OperationCanceledException>();

        await Assert.That(scenario.Board.NeedsYou.Select(static item => item.Id))
            .IsEquivalentTo(["ask_first", "ask_gone", "ask_last", "form_first", "form_gone", "form_last"], CollectionOrdering.Matching);
        await Assert.That(changed).IsEqualTo(0);
    }

    private static readonly FormStringField Pick = new() { Key = "pick", Options = [new FormOption { Label = "A", Value = "a" }] };

    private static FormInfo Form() => new() { Id = "form_first", SessionId = "ses_0", Title = "question", Fields = [Pick] };
    private static SessionFormReplyRequest Reply() => new() { Answer = new Dictionary<string, FormValue>(StringComparer.Ordinal) };
    private static OpenCodeApiException Unauthorized() => new("unauthorized", 401, new UnauthorizedError { Message = "unauthorized" }, null);
    private static EventServerConnected Connected() => new() { Id = "connected", Data = new Dictionary<string, System.Text.Json.JsonElement>(StringComparer.Ordinal) };
    private static SessionInfo Session(int index, string directory) => new()
    {
        Id = "ses_" + index.ToString(CultureInfo.InvariantCulture),
        ProjectId = "project",
        Title = directory,
        Cost = 0,
        Tokens = new TokenUsageInfo { Input = 0, Output = 0, Reasoning = 0, Cache = new TokenUsageInfoCache { Read = 0, Write = 0 } },
        Time = new SessionInfoTime { Created = index, Updated = index },
        Location = new LocationPublicRef { Directory = directory },
    };

    private sealed class BoardScenario : OpenCodeClient, IAsyncDisposable
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
    }

    private sealed class BoardEvents : EventsClient
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

    private sealed class BoardLogger : ILogger<SessionBoard>
    {
        public Exception? LastFailure { get; private set; }
        public IDisposable? BeginScope<TState>(TState state) where TState : notnull => null;
        public bool IsEnabled(LogLevel logLevel) => true;
        public void Log<TState>(LogLevel logLevel, EventId eventId, TState state, Exception? exception,
            Func<TState, Exception?, string> formatter) => LastFailure = exception;
    }

    private sealed class BoardSessions : SessionsClient
    {
        public List<string> Trace { get; set; } = [];
        public int Refreshes { get; private set; }
        private bool _descending;
        public IReadOnlyList<SessionInfo> Items { get; set; } = [Session(0, "first"), Session(1, "gone"), Session(2, "last")];
        public IReadOnlySet<string> Active { get; set; } = new HashSet<string>(["ses_0", "ses_1", "ses_2"], StringComparer.Ordinal);
        public OpenCodeApiException? ReplyFailure { get; set; }
        public OpenCodeException? FormFailure { get; set; }
        public TaskCompletionSource ListingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ListingRelease { get; set; }
        public override SessionClient GetSessionClient(string sessionId) => new BoardSession(this, sessionId);
        public override Task<SessionActiveResponse> GetActiveAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionActiveResponse { Status = 200, Active = Active.ToDictionary(static id => id, static _ => new SessionActive { Type = "running" }, StringComparer.Ordinal) });
        public override async Task<SessionListResponse> ListSessionsAsync(SessionListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
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

    private sealed class BoardSession(BoardSessions owner, string sessionId) : SessionClient
    {
        public override Task<SessionResponse> GetAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionResponse { Status = 200, Session = owner.Items.Single(item => item.Id == sessionId) });

        public override Task<SessionFormReplyResponse> ReplyToFormAsync(string formId, SessionFormReplyRequest request,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return owner.FormFailure is { } failure ? Task.FromException<SessionFormReplyResponse>(failure)
                : Task.FromResult(new SessionFormReplyResponse { Status = 204 });
        }

        public override Task<SessionPermissionReplyResponse> ReplyToPermissionAsync(string requestId, SessionPermissionReplyRequest request,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            owner.ReplyFailure is { } failure ? Task.FromException<SessionPermissionReplyResponse>(failure) : Task.FromResult(new SessionPermissionReplyResponse { Status = 204 });
    }

    private sealed class BoardPermissions : PermissionsClient
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

    private sealed class BoardForms : FormsClient
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
