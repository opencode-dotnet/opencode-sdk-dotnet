using System.Globalization;
using System.Runtime.CompilerServices;
using System.Threading.Channels;
using Microsoft.Extensions.Logging.Abstractions;
using OpenCode.Playground.Hq.Features.Sessions;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;

namespace OpenCode.Playground.Hq.Tests.Features.Sessions;

public sealed class SessionBoardTests
{
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
        scenario.EventCalls.Input.Writer.TryWrite(static () => throw Unauthorized());
        await scenario.SettleAsync();
        await Assert.That(scenario.Board.Connected).IsFalse();
        scenario.SessionCalls.Items = [Session(8, "first")];
        scenario.SessionCalls.Active = new HashSet<string>(["ses_8"], StringComparer.Ordinal);
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
        public BoardScenario() => Board = new SessionBoard(this, NullLogger<SessionBoard>.Instance);
        public SessionBoard Board { get; }
        public BoardSessions SessionCalls { get; } = new();
        public BoardPermissions PermissionCalls { get; } = new();
        public BoardForms FormCalls { get; } = new();
        public BoardEvents EventCalls { get; } = new();
        public override SessionsClient Sessions => SessionCalls;
        public override PermissionsClient Permissions => PermissionCalls;
        public override FormsClient Forms => FormCalls;
        public override EventsClient Events => EventCalls;

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

        public async Task SettleAsync()
        {
            // A pull after handling an event, or the service ending, bounds the observation; no sleep decides a verdict.
            await Task.WhenAny(EventCalls.Pulls.Reader.ReadAsync().AsTask(), Board.ExecuteTask ?? Task.CompletedTask).WaitAsync(TimeSpan.FromSeconds(10));
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
        public Channel<Func<IEvent>> Input { get; } = Channel.CreateUnbounded<Func<IEvent>>();
        public Channel<byte> Pulls { get; } = Channel.CreateUnbounded<byte>();
        public override async IAsyncEnumerable<IEvent> SubscribeAsync(EventSubscribeRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, [EnumeratorCancellation] CancellationToken cancellationToken = default)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                Pulls.Writer.TryWrite(0);
                yield return (await Input.Reader.ReadAsync(cancellationToken))();
            }
        }
    }

    private sealed class BoardSessions : SessionsClient
    {
        private bool _descending;
        public IReadOnlyList<SessionInfo> Items { get; set; } = [Session(0, "first"), Session(1, "gone"), Session(2, "last")];
        public IReadOnlySet<string> Active { get; set; } = new HashSet<string>(["ses_0", "ses_1", "ses_2"], StringComparer.Ordinal);
        public OpenCodeApiException? ReplyFailure { get; set; }
        public TaskCompletionSource ListingStarted { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource? ListingRelease { get; set; }
        public override SessionClient GetSessionClient(string sessionId) => new BoardSession(this);
        public override Task<SessionActiveResponse> GetActiveAsync(OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default) =>
            Task.FromResult(new SessionActiveResponse { Status = 200, Active = Active.ToDictionary(static id => id, static _ => new SessionActive { Type = "running" }, StringComparer.Ordinal) });
        public override async Task<SessionListResponse> ListSessionsAsync(SessionListRequest? request = null,
            OpenCodeRequestOptions? requestOptions = null, CancellationToken cancellationToken = default)
        {
            if (ListingRelease is { } release)
            {
                ListingStarted.TrySetResult();
                await release.Task.WaitAsync(cancellationToken);
            }

            if (request?.Cursor is null)
            {
                _descending = request?.Order == ListOrder.Descending;
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

    private sealed class BoardSession(BoardSessions owner) : SessionClient
    {
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
                Id = "form_" + request?.Location?.Directory, SessionId = "ses_0", Title = "question", Fields = [],
            }]
            });
        }
    }
}
