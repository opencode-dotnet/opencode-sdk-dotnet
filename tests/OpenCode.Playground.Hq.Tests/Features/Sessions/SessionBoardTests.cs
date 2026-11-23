using System.Globalization;
using OpenCode.Playground.Hq.Tests.Features.Sessions.Support;
using OpenCode.Sdk;
using OpenCode.Sdk.Models;
using TUnit.Assertions.Enums;
using static OpenCode.Playground.Hq.Tests.Features.Sessions.Support.BoardScenario.BoardData;

namespace OpenCode.Playground.Hq.Tests.Features.Sessions;

public sealed class SessionBoardTests
{
    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SessionBoard_Should_Remove_The_Row_And_Its_Asks_On_Deleted_Or_Archived(bool archived)
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        await scenario.AskElsewhereAsync();
        var changed = 0;
        scenario.Board.Changed += (_, _) => changed++;
        IEvent item = archived
            ? new SessionArchived
            {
                Id = "archived",
                Created = 2,
                Durable = new() { AggregateId = "ses_0", Seq = 2, Version = 1 },
                Data = new() { SessionId = "ses_0" },
            }
            : new SessionDeleted
            {
                Id = "deleted",
                Created = 2,
                Durable = new() { AggregateId = "ses_0", Seq = 2, Version = 1 },
                Data = new() { SessionId = "ses_0" },
            };
        await scenario.SendAsync(item);

        await Assert.That(scenario.Board.Sessions.Select(static row => row.Session.Id))
            .IsEquivalentTo(["ses_2", "ses_1"], CollectionOrdering.Matching);
        await Assert.That(scenario.Board.NeedsYou.Select(static ask => ask.Id)).IsEquivalentTo(["ask_other"], CollectionOrdering.Matching);
        await Assert.That(changed).IsEqualTo(1);
    }

    [Test]
    public async Task SessionBoard_Should_Read_And_Restore_An_Unarchived_Session()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        await scenario.Board.ArchiveAsync("ses_0", CancellationToken.None);
        scenario.SessionCalls.Items = [Session(0, "restored")];
        var changed = 0;
        scenario.Board.Changed += (_, _) => changed++;
        await scenario.SendAsync(new SessionUnarchived
        {
            Id = "restored",
            Created = 3,
            Durable = new() { AggregateId = "ses_0", Seq = 3, Version = 1 },
            Data = new() { SessionId = "ses_0" },
        });

        await Assert.That(scenario.Board.Sessions.Select(static row => row.Session.Id))
            .IsEquivalentTo(["ses_2", "ses_1", "ses_0"], CollectionOrdering.Matching);
        await Assert.That(scenario.Board.Find("ses_0")?.Title).IsEqualTo("restored");
        await Assert.That(scenario.SessionCalls.ReadSessions).IsEquivalentTo(["ses_0"], CollectionOrdering.Matching);
        await Assert.That(changed).IsEqualTo(1);
    }

    [Test]
    [Arguments(true, false)]
    [Arguments(true, true)]
    [Arguments(false, false)]
    [Arguments(false, true)]
    public async Task ArchiveAsync_And_DeleteAsync_Should_Remove_Only_That_Row_And_Its_Asks_Or_Report_The_Refusal(bool archive, bool refused)
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        await scenario.AskElsewhereAsync();
        var failure = refused ? Unauthorized() : null;
        scenario.SessionCalls.ActionFailure = failure;
        var changed = 0;
        scenario.Board.Changed += (_, _) => changed++;
        if (archive)
        {
            await scenario.Board.ArchiveAsync("ses_0", CancellationToken.None);
            await Assert.That(scenario.SessionCalls.UpdatedSession).IsEqualTo("ses_0");
            await Assert.That(scenario.SessionCalls.UpdateRequest?.Archived.Value).IsTrue();
            await Assert.That(scenario.SessionCalls.UpdateRequest?.Title.IsSet).IsFalse();
        }
        else
        {
            await scenario.Board.DeleteAsync("ses_0", CancellationToken.None);
            await Assert.That(scenario.SessionCalls.RemovedSession).IsEqualTo("ses_0");
        }

        string[] sessions = refused ? ["ses_2", "ses_1", "ses_0"] : ["ses_2", "ses_1"];
        string[] asks = refused ? ["ask_first", "ask_gone", "ask_last", "ask_other", "form_first", "form_gone", "form_last"] : ["ask_other"];
        await Assert.That(scenario.Board.Sessions.Select(static row => row.Session.Id)).IsEquivalentTo(sessions, CollectionOrdering.Matching);
        await Assert.That(scenario.Board.NeedsYou.Select(static ask => ask.Id)).IsEquivalentTo(asks, CollectionOrdering.Matching);
        var error = archive ? "Request failed" : "Failed to delete session";
        await Assert.That(scenario.Board.LastError).IsEqualTo(refused ? error : null);
        await Assert.That(scenario.Logger.LastFailure).IsSameReferenceAs(failure);
        await Assert.That(changed).IsEqualTo(1);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RenameAsync_Should_Update_That_Title_Or_Report_The_Refusal(bool refused)
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        var failure = refused ? Unauthorized() : null;
        scenario.SessionCalls.ActionFailure = failure;
        var changed = 0;
        scenario.Board.Changed += (_, _) => changed++;
        await scenario.Board.RenameAsync("ses_0", "Renamed", CancellationToken.None);

        await Assert.That(scenario.Board.Find("ses_0")?.Title).IsEqualTo(refused ? "first" : "Renamed");
        await Assert.That(scenario.SessionCalls.UpdatedSession).IsEqualTo("ses_0");
        await Assert.That(scenario.SessionCalls.UpdateRequest?.Title).IsEqualTo("Renamed");
        await Assert.That(scenario.Board.LastError).IsEqualTo(refused ? "Request failed" : null);
        await Assert.That(scenario.Logger.LastFailure).IsSameReferenceAs(failure);
        await Assert.That(changed).IsEqualTo(1);
    }

    [Test]
    public async Task SessionBoard_Should_Seed_Only_Unarchived_Sessions()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();

        await Assert.That(scenario.SessionCalls.ListRequest?.Archived).IsFalse();
    }

    [Test]
    public async Task AnswerAsync_Should_Send_Always_For_The_Request_And_Session()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        var item = scenario.Board.NeedsYou.Single(static ask => ask.Id == "ask_first");
        await scenario.Board.AnswerAsync(item, PermissionReply.Always, CancellationToken.None);

        await Assert.That(scenario.SessionCalls.PermissionSession).IsEqualTo("ses_0");
        await Assert.That(scenario.SessionCalls.PermissionId).IsEqualTo("ask_first");
        await Assert.That(scenario.SessionCalls.Decision?.ToString()).IsEqualTo("always");
        await Assert.That(scenario.Board.NeedsYou.Select(static ask => ask.Id))
            .IsEquivalentTo(["ask_gone", "ask_last", "form_first", "form_gone", "form_last"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task DismissFormAsync_Should_Cancel_The_Form_Or_Report_The_Refusal(bool refused)
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        scenario.SessionCalls.FormFailure = refused ? Unauthorized() : null;
        await scenario.Board.DismissFormAsync(Form(), CancellationToken.None);

        await Assert.That(scenario.SessionCalls.CancelSession).IsEqualTo("ses_0");
        await Assert.That(scenario.SessionCalls.CancelId).IsEqualTo("form_first");
        await Assert.That(scenario.Board.LastError).IsEqualTo(refused ? "Could not answer the question: unauthorized" : null);
        var expected = refused ? new[] { "ask_first", "ask_gone", "ask_last", "form_first", "form_gone", "form_last" }
            : ["ask_first", "ask_gone", "ask_last", "form_gone", "form_last"];
        await Assert.That(scenario.Board.NeedsYou.Select(static ask => ask.Id)).IsEquivalentTo(expected, CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SessionBoard_Should_Relist_Asks_When_A_Turn_Ends_Except_On_Shutdown(bool shutdown)
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        scenario.SessionCalls.Pending = ["ask_last"];
        await scenario.SendAsync(new SessionExecutionInterrupted
        {
            Id = "interrupted",
            Created = 2,
            Durable = new() { AggregateId = "ses_0", Seq = 2, Version = 1 },
            Data = new()
            {
                SessionId = "ses_0",
                Reason = shutdown ? SessionExecutionInterruptedDataReason.Shutdown : SessionExecutionInterruptedDataReason.User,
            },
        });

        string[] expected = shutdown ? ["ask_first", "ask_gone", "ask_last", "form_first", "form_gone", "form_last"] : ["ask_last"];
        string[] relisted = shutdown ? [] : ["ses_0"];
        await Assert.That(scenario.Board.NeedsYou.Select(static ask => ask.Id)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(scenario.SessionCalls.Relisted).IsEquivalentTo(relisted, CollectionOrdering.Matching);
    }

    [Test]
    public async Task ResyncAsync_Should_Replace_Only_That_Sessions_Asks_With_The_Servers_List()
    {
        await using var scenario = new BoardScenario();
        await scenario.StartAsync();
        await scenario.SendAsync(new PermissionAsked
        {
            Id = "asked",
            Created = 2,
            Data = new() { Id = "ask_other", SessionId = "ses_1", Action = "edit", Resources = ["other"] },
        });
        scenario.SessionCalls.Pending = ["ask_last"];
        await scenario.Board.ResyncAsync("ses_0", CancellationToken.None);

        await Assert.That(scenario.Board.NeedsYou.Select(static ask => ask.Id)).IsEquivalentTo(["ask_last", "ask_other"], CollectionOrdering.Matching);
        await Assert.That(scenario.SessionCalls.Relisted).IsEquivalentTo(["ses_0"], CollectionOrdering.Matching);
    }

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

}
