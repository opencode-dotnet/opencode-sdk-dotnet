using System.Net;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Sessions;

public sealed class SessionClientContractTests
{
    [Test]
    public async Task EnumerateMessagesAsync_Should_Apply_Location_To_Every_Page()
    {
        using var scenario = ContractScenario.RespondingWithPages(
            WireBodyData.Page("", next: "next_1"),
            WireBodyData.Page("", next: "next_2"),
            WireBodyData.Page(""));
        var messages = scenario.Client.Sessions.GetSessionClient("ses_100").EnumerateMessagesAsync(
            new SessionMessageListRequest { Order = ListOrder.Ascending },
            new OpenCodeRequestOptions { Location = new LocationSelector { Directory = "/override/ç" } });
        await Assert.That(scenario.Requests).IsEmpty();
        var count = 0;
        await foreach (var page in messages.Pages.WithCancellation(CancellationToken.None))
        {
            await Assert.That(page.Messages).IsEmpty();
            count++;
        }

        await Assert.That(count).IsEqualTo(3);
        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.Query))
            .IsEquivalentTo(["?order=asc", "?cursor=next_1", "?cursor=next_2"], CollectionOrdering.Matching);
        foreach (var request in scenario.Requests)
        {
            await Assert.That(request.Headers["x-opencode-directory"]).IsEqualTo("%2Foverride%2F%C3%A7");
        }
    }

    [Test]
    public async Task EnumerateMessagesAsync_Should_Refuse_NoThrow_At_The_Call()
    {
        using var scenario = ContractScenario.Responding();

        var exception = await Assert.That(() => scenario.Client.Sessions.GetSessionClient("ses_100")
            .EnumerateMessagesAsync(requestOptions: OpenCodeRequestOptions.NoThrow)).Throws<ArgumentException>();

        await Assert.That(exception!.GetType()).IsEqualTo(typeof(ArgumentException));
        await Assert.That(exception.ParamName).IsEqualTo("requestOptions");
        await Assert.That(scenario.Requests).IsEmpty();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task ReadLogAsync_Should_Apply_NetworkTimeout_Only_While_Opening(bool live)
    {
        var time = new ManualTimeProvider();
        var window = TimeSpan.FromSeconds(3);
        using var blocked = new BlockingContent();
        using var body = new LiveEventBody(WireBodyData.Frames(WireBodyData.LogSyncedEvent));
        using var handler = new RecordingHttpHandler(_ => live
            ? ScriptedEventServer.Stream(body)
            : new HttpResponseMessage(HttpStatusCode.InternalServerError) { Content = blocked });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, networkTimeout: TimeSpan.FromMinutes(1), time: time);
        using var caller = new CancellationTokenSource();
        await using var log = new SessionClient(pipeline, "ses_9").ReadLogAsync(
            new SessionLogRequest(), new OpenCodeRequestOptions { NetworkTimeout = window }, caller.Token).GetAsyncEnumerator();
        if (live)
        {
            await Assert.That(await ReadNextLogAsync(log).WaitAsync(HangGuard.Bound)).IsTrue();
            await Assert.That(log.Current).IsTypeOf<EventLogSynced>();
            var next = ReadNextLogAsync(log);
            time.Advance(window + TimeSpan.FromMinutes(2));
            await Assert.That(next.IsCompleted).IsFalse();
            await Assert.That(body.IsDisposed).IsFalse();
            body.Append(WireBodyData.Frames(WireBodyData.UnknownLogEvent));
            await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
            await Assert.That(log.Current).IsTypeOf<UnknownSessionLogItem>();
            body.End();
            await Assert.That(await ReadNextLogAsync(log).WaitAsync(HangGuard.Bound)).IsFalse();
        }
        else
        {
            var opening = ReadNextLogAsync(log);
            await blocked.ReadStarted.WaitAsync(HangGuard.Bound);
            // Drain the pending read even when the wrong timeout was forwarded.
            if (time.ArmedIn(window) == 0)
            {
                await caller.CancelOnWorkerAsync();
                _ = await Assert.That(opening.WaitAsync(HangGuard.Bound)).Throws<OperationCanceledException>();
            }

            await Assert.That(time.ArmedIn(window)).IsEqualTo(1);
            await time.WaitForArmedAsync(window, HangGuard.Bound);
            time.Advance(window - TimeSpan.FromTicks(1));
            await Assert.That(opening.IsCompleted).IsFalse();
            time.Advance(TimeSpan.FromTicks(1));
            _ = await Assert.That(opening.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();
            await blocked.ReadCompleted.WaitAsync(HangGuard.Bound);
            await Assert.That(blocked.IsDisposed).IsTrue();
        }
    }

    private static readonly SessionUpdateRequest EmptyRuleset = new() { Permissions = new Optional<IReadOnlyList<PermissionRule>?>([]), };

    private static Task<bool> ReadNextLogAsync(IAsyncEnumerator<ISessionLogItem> log) => log.MoveNextAsync().AsTask();

    [Test]
    [Arguments(WireBodyData.StreamFailureCause, "Die")]
    [Arguments(WireBodyData.StreamInterruptNullCause, "Interrupt")]
    [Arguments(WireBodyData.StreamInterruptNumberCause, "Interrupt")]
    [Arguments(WireBodyData.UnknownStreamFailureCause, "FutureCause")]
    public async Task ReadLogAsync_Should_Expose_Each_Causes_Wire_Tag_Through_The_Marker(string body, string tag)
    {
        using var scenario = ContractScenario.Responding(_ => ScriptedEventServer.StreamOf(
            WireBodyData.NamedFrame("effect/http-api/stream/failure", body), ends: true));
        await using var log = scenario.Client.Sessions.GetSessionClient("ses_9").ReadLogAsync().GetAsyncEnumerator();

        var exception = await Assert.That(async () => _ = await log.MoveNextAsync()).Throws<OpenCodeStreamFailureException>();

        var cause = exception!.Causes.Single();
        await Assert.That(cause.Tag).IsEqualTo(tag);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RequestOptions_Should_Name_The_Public_Parameter_On_Stream_And_OneShot_Doors(bool stream)
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, string.Empty);
        var session = scenario.Client.Sessions.GetSessionClient("ses_100");
        if (stream)
        {
            var refusal = await Assert.That(() => session.ReadLogAsync(requestOptions: OpenCodeRequestOptions.NoThrow))
                .Throws<ArgumentException>();
            await Assert.That(refusal!.GetType()).IsEqualTo(typeof(ArgumentException));
            await Assert.That(refusal.ParamName).IsEqualTo("requestOptions");
            await Assert.That(scenario.Requests).IsEmpty();
            return;
        }

        var options = new OpenCodeRequestOptions { ErrorBehavior = (ErrorBehavior)42 };

        var exception = await Assert.That(async () => _ = await session.GetAsync(requestOptions: options))
            .Throws<ArgumentOutOfRangeException>();

        await Assert.That(exception!.GetType()).IsEqualTo(typeof(ArgumentOutOfRangeException));
        await Assert.That(exception.ParamName).IsEqualTo("requestOptions");
        await Assert.That(exception.ActualValue).IsEqualTo(options.ErrorBehavior);
        await Assert.That(scenario.Requests).IsEmpty();
    }

    [Test]
    public async Task GetAsync_Should_Return_The_Typed_Session()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-session.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(payload));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").GetAsync();

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.Session.Id).IsEqualTo("ses_100");
        await Assert.That(response.Session.Location.Directory).IsEqualTo("/repo");
        await Assert.That(response.Session.Tokens.Cache.Read).IsEqualTo(1);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100"));
    }

    [Test]
    public async Task GetAsync_Should_Treat_A_Null_Datum_As_A_Protocol_Failure()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope("null"));

        _ = await Assert
            .That(async () => _ = await scenario.Client.Sessions.GetSessionClient("ses_100").GetAsync())
            .Throws<OpenCodeTransportException>();
    }

    [Test]
    public async Task GetAsync_Should_Collapse_An_Explicit_Null_Parent()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.null-parent-session.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(payload));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").GetAsync();

        await Assert.That(response.Session.ParentId).IsNull();
    }

    [Test]
    public async Task GetAsync_Should_Treat_A_Numeric_Enum_Status_As_A_Malformed_Success()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.numeric-status-session.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(payload));

        _ = await Assert
            .That(async () => _ = await scenario.Client.Sessions.GetSessionClient("ses_100").GetAsync())
            .Throws<OpenCodeTransportException>();
    }

    [Test]
    public async Task ListMessagesAsync_Should_Return_The_Typed_Page_With_Its_Cursor()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-session-message.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Page(payload, previous: "cur_0"));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").ListMessagesAsync(new SessionMessageListRequest
        {
            Limit = 2,
            Order = ListOrder.Ascending,
        });

        await Assert.That(response.Messages.Single()).IsTypeOf<SessionMessageUser>();
        await Assert.That(response.Cursor.Previous).IsEqualTo("cur_0");
        await Assert.That(response.Cursor.Next).IsNull();
        await Assert.That(scenario.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/session/ses_100/message?limit=2&order=asc");
    }

    [Test]
    public async Task ListMessagesAsync_Should_Throw_The_Declared_500_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.InternalServerError, WireBodyData.UnknownError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Sessions.GetSessionClient("ses_100").ListMessagesAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(500);
        await Assert.That(exception.Error).IsTypeOf<UnknownError>();
        await Assert.That(((UnknownError)exception.Error!).Message).IsEqualTo("boom");
    }

    [Test]
    public async Task ListMessagesAsync_Should_Return_The_500_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.InternalServerError, WireBodyData.UnknownError);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100")
            .ListMessagesAsync(requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(500);
        await Assert.That(response.Error).IsTypeOf<UnknownError>();
        await Assert.That(response.RawBody).Contains("boom");
    }

    [Test]
    public async Task ListMessagesAsync_Should_Send_Order_Combined_With_Cursor()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Page(""));

        _ = await scenario.Client.Sessions.GetSessionClient("ses_100").ListMessagesAsync(new SessionMessageListRequest
        {
            Order = ListOrder.Ascending,
            Cursor = "cur_1",
        });

        await Assert.That(scenario.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/session/ses_100/message?order=asc&cursor=cur_1");
    }

    [Test]
    public async Task ListMessagesAsync_Should_Send_The_Type_Filter_As_Its_Wire_Name()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Page(""));

        _ = await scenario.Client.Sessions.GetSessionClient("ses_100").ListMessagesAsync(new SessionMessageListRequest
        {
            Type = SessionMessageListRequestType.LocationSwitched,
        });

        // The hyphenated wire name, not the C# member name, is what the server filters on.
        await Assert.That(scenario.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/session/ses_100/message?type=location-switched");
    }

    [Test]
    public async Task EnumerateMessagesAsync_Should_Lazily_Follow_Opaque_Next_Cursors()
    {
        const string initialCursor = "cur_start";
        const string firstNext = "cur_next_1";
        const string secondNext = "cur_next_2";
        var fixtureLoader = new FixtureLoader();
        var user = fixtureLoader.LoadJson("Serialization.known-session-message.json");
        var shell = fixtureLoader.LoadJson("Serialization.known-session-message-shell.json");
        using var scenario = ContractScenario.RespondingWithPages(
            WireBodyData.Page("", next: firstNext),
            WireBodyData.Page(user, next: secondNext),
            WireBodyData.Page(shell));
        var messages = scenario.Client.Sessions.GetSessionClient("ses_100").EnumerateMessagesAsync(new SessionMessageListRequest
        {
            Limit = 2,
            Order = ListOrder.Ascending,
            Cursor = initialCursor,
        }, cancellationToken: CancellationToken.None);

        await Assert.That(scenario.Requests).IsEmpty();

        var received = new List<ISessionMessageInfo>();
        await foreach (var message in messages.WithCancellation(CancellationToken.None))
        {
            received.Add(message);
        }

        await Assert.That(received.Count).IsEqualTo(2);
        await Assert.That(received[0]).IsTypeOf<SessionMessageUser>();
        await Assert.That(received[1]).IsTypeOf<SessionMessageShell>();
        await Assert.That(scenario.Requests.Select(static request => request.RequestUri!.AbsoluteUri)).IsEquivalentTo([
                $"http://localhost:4096/api/session/ses_100/message?limit=2&order=asc&cursor={initialCursor}",
                $"http://localhost:4096/api/session/ses_100/message?limit=2&cursor={firstNext}",
                $"http://localhost:4096/api/session/ses_100/message?limit=2&cursor={secondNext}",
            ], StringComparer.Ordinal, CollectionOrdering.Matching);
    }

    [Test]
    public async Task EnumerateMessagesAsync_Should_Throw_The_Declared_Error_When_A_Later_Page_Fails()
    {
        const string nextCursor = "cur_invalid";
        var payload = new FixtureLoader().LoadJson("Serialization.known-session-message.json");
        var responses = new Queue<(HttpStatusCode Status, string Body)>(
        [
            (HttpStatusCode.OK, WireBodyData.Page(payload, next: nextCursor)),
            (HttpStatusCode.BadRequest, WireBodyData.InvalidCursorError),
        ]);
        using var scenario = ContractScenario.Responding(_ =>
        {
            var response = responses.Dequeue();
            return new HttpResponseMessage(response.Status) { Content = new StringContent(response.Body), };
        });
        await using var enumerator = scenario.Client.Sessions.GetSessionClient("ses_100")
            .EnumerateMessagesAsync(cancellationToken: CancellationToken.None)
            .GetAsyncEnumerator();

        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await Assert.That(enumerator.Current).IsTypeOf<SessionMessageUser>();

        var exception = await Assert
            .That(async () => _ = await enumerator.MoveNextAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception?.Error).IsTypeOf<InvalidCursorError>();
        await Assert.That(scenario.Requests.Count).IsEqualTo(2);
        await Assert.That(scenario.Requests[1].RequestUri!.Query).IsEqualTo($"?cursor={nextCursor}");
    }

    [Test]
    public async Task EnumerateMessagesAsync_Should_Observe_Cancellation_Between_Buffered_Items()
    {
        var fixtureLoader = new FixtureLoader();
        var user = fixtureLoader.LoadJson("Serialization.known-session-message.json");
        var shell = fixtureLoader.LoadJson("Serialization.known-session-message-shell.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Page($"{user},{shell}"));
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = scenario.Client.Sessions.GetSessionClient("ses_100")
            .EnumerateMessagesAsync(cancellationToken: cancellation.Token)
            .GetAsyncEnumerator();

        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await cancellation.CancelOnWorkerAsync();

        _ = await Assert
            .That(async () => _ = await enumerator.MoveNextAsync())
            .Throws<OperationCanceledException>();
        await Assert.That(scenario.Requests.Count).IsEqualTo(1);
    }

    [Test]
    [NotInParallel]
    public async Task EnumerateMessagesAsync_Should_Send_The_Caller_Token_To_Each_Page_Request()
    {
        const string nextCursor = "cur_next";
        var payload = new FixtureLoader().LoadJson("Serialization.known-session-message.json");
        var responses = new Queue<string>(
        [
            WireBodyData.Page(payload, next: nextCursor),
            WireBodyData.Page(""),
        ]);
        using var scenario = ContractScenario.Responding(_ => new HttpResponseMessage(HttpStatusCode.OK)
        {
            Content = new StringContent(responses.Dequeue()),
        });
        using var cancellation = new CancellationTokenSource();
        await using var enumerator = scenario.Client.Sessions.GetSessionClient("ses_100")
            .EnumerateMessagesAsync(cancellationToken: cancellation.Token)
            .GetAsyncEnumerator();

        await Assert.That(await enumerator.MoveNextAsync()).IsTrue();
        await cancellation.CancelOnWorkerAsync();

        bool? moved = null;
        OperationCanceledException? cancellationException = null;
        try
        {
            moved = await enumerator.MoveNextAsync().AsTask().WaitAsync(TimeSpan.FromSeconds(1));
        }
        catch (OperationCanceledException exception)
        {
            cancellationException = exception;
        }

        await Assert.That(moved is false || cancellationException is not null).IsTrue();
        await Assert.That(scenario.CancellationTokens.Count).IsEqualTo(2);
        await Assert.That(scenario.CancellationTokens[1].IsCancellationRequested).IsTrue();
    }

    [Test]
    public async Task RemoveSessionAsync_Should_Treat_The_204_As_A_Bodiless_Success()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").RemoveAsync();

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100"));
    }

    [Test]
    public async Task RenameSessionAsync_Should_Send_The_Typed_Body()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").UpdateAsync(new SessionUpdateRequest
        {
            Title = "Renamed session",
        });

        await Assert.That(response.Status).IsEqualTo(204);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method.Method).IsEqualTo("PATCH");
        await Assert.That(request.RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/session/ses_100");
        await Assert.That(request.Body).IsEqualTo("{\"title\":\"Renamed session\"}");
    }

    [Test]
    public async Task PostForkAsync_Should_Send_The_Before_Message_Id()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-session.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(payload));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").ForkAsync(new SessionForkRequest
        {
            Before = "msg_1",
        });

        await Assert.That(response.Fork.Id).IsEqualTo("ses_100");
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/fork"));
        await Assert.That(request.Body).IsEqualTo("{\"before\":\"msg_1\"}");
    }

    [Test]
    public async Task PostCompactAsync_Should_Send_The_Empty_Body_When_Omitted()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-inbox-compaction.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(payload));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").CompactAsync();

        await Assert.That(response.Compact.Id).IsEqualTo("inb_1");
        await Assert.That(response.Compact.Delivery).IsEqualTo(SessionInboxDelivery.Queue);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.ContentType).IsEqualTo("application/json");
        await Assert.That(request.Body).IsEqualTo("{}");
    }

    [Test]
    public async Task GetExportAsync_Should_Compose_The_Sanitize_Query()
    {
        var session = new FixtureLoader().LoadJson("Serialization.known-session.json");
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK,
            WireBodyData.Envelope($"{{\"info\":{session},\"messages\":[]}}"));

        var response = await scenario.Client.Experimental.GetSessionExportAsync("ses_100", new ExperimentalSessionExportRequest
        {
            Sanitize = true,
        });

        await Assert.That(response.SessionExport.Info.Id).IsEqualTo("ses_100");
        await Assert.That(response.SessionExport.Messages).IsEmpty();
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/experimental/session/ses_100/export?sanitize=true"));
    }

    [Test]
    public async Task DeleteInboxCancelAsync_Should_Compose_Both_Path_Parameters_On_The_204()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").CancelInboxAsync("inb_1");

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/inbox/inb_1"));
    }

    [Test]
    public async Task DeleteInboxCancelAsync_Should_Throw_The_Undeclared_409_As_An_Unknown_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Conflict, WireBodyData.ConflictError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Sessions.GetSessionClient("ses_100").CancelInboxAsync("inb_1"))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(409);
        await Assert.That(exception.Error).IsTypeOf<UnknownOpenCodeError>();
    }

    [Test]
    public async Task PostInterruptAsync_Should_Send_A_Bodiless_Post()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.SessionInterrupted);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").InterruptAsync();

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Interrupt.Interrupted).IsTrue();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/interrupt"));
        await Assert.That(request.Body).IsNull();
        await Assert.That(request.ContentType).IsNull();
    }

    [Test]
    public async Task CancelFormAsync_Should_Throw_The_Declared_409_Error()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.Conflict,
            "{\"_tag\":\"FormAlreadySettledError\",\"id\":\"frm_1\",\"message\":\"settled\"}");

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Sessions.GetSessionClient("ses_100").CancelFormAsync("frm_1"))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(409);
        await Assert.That(exception.Error).IsTypeOf<FormAlreadySettledError>();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/form/frm_1"));
        await Assert.That(request.Body).IsNull();
    }

    [Test]
    public async Task CancelFormAsync_Should_Send_The_Message_As_An_Escaped_Query_Value()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").CancelFormAsync(
            "frm_1",
            new SessionFormCancelRequest { Message = "no longer needed & done" });

        await Assert.That(response.Status).IsEqualTo(204);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(request.RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/session/ses_100/form/frm_1?message=no%20longer%20needed%20%26%20done");
        await Assert.That(request.Body).IsNull();
    }

    [Test]
    public async Task CancelFormAsync_Should_Omit_The_Query_When_The_Message_Is_Unset()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        _ = await scenario.Client.Sessions.GetSessionClient("ses_100").CancelFormAsync(
            "frm_1",
            new SessionFormCancelRequest());

        await Assert.That(scenario.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/session/ses_100/form/frm_1");
    }

    [Test]
    public async Task GetFormAsync_Should_Read_The_Cancellation_Message()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK,
            WireBodyData.Envelope(WireBodyData.FormDetail(WireBodyData.FormCancelledState)));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").GetFormAsync("frm_1");

        await Assert.That(response.Form.State).IsTypeOf<FormStateCancelled>();
        await Assert.That(((FormStateCancelled)response.Form.State).Message).IsEqualTo("stop");
    }

    [Test]
    public async Task PutInstructionsEntryAsync_Should_Send_The_Typed_Body_On_The_Put()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);
        using var value = JsonDocument.Parse("\"be terse\"");

        var response = await scenario.Client.Experimental.SetSessionInstructionsEntryAsync("ses_100",
            "style",
            new ExperimentalSessionInstructionsEntryPutRequest { Value = value.RootElement });

        await Assert.That(response.Status).IsEqualTo(204);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/experimental/session/ses_100/instructions/entries/style"));
        await Assert.That(request.Body).IsEqualTo("{\"value\":\"be terse\"}");
    }

    [Test]
    public async Task PutInstructionsEntryAsync_Should_Throw_The_Declared_413_Error()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.RequestEntityTooLarge,
            "{\"_tag\":\"InstructionEntryValueTooLargeError\",\"actualBytes\":2048,\"maxBytes\":1024,\"message\":\"too large\"}");
        using var value = JsonDocument.Parse("\"be terse\"");

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Experimental.SetSessionInstructionsEntryAsync("ses_100",
                "style",
                new ExperimentalSessionInstructionsEntryPutRequest { Value = value.RootElement }))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(413);
        var error = (InstructionEntryValueTooLargeError)exception.Error!;
        await Assert.That(error.ActualBytes).IsEqualTo(2048);
        await Assert.That(error.MaxBytes).IsEqualTo(1024);
    }

    [Test]
    public async Task CreatePermissionAsync_Should_Send_The_Typed_Body_And_Return_The_Effect()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-permission.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(payload));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").CreatePermissionAsync(
            new SessionPermissionCreateRequest
            {
                Action = "read",
                Resources = ["file:///repo/a.txt"],
            });

        await Assert.That(response.Permission.Id).IsEqualTo("perm_1");
        await Assert.That(response.Permission.Effect).IsEqualTo(PermissionEffect.Allow);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/permission"));
        await Assert.That(request.Body).IsEqualTo("{\"action\":\"read\",\"resources\":[\"file:///repo/a.txt\"]}");
    }

    [Test]
    public async Task GetContextAsync_Should_Return_The_Typed_Context_Messages()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-session-message.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope($"[{payload}]"));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").GetContextAsync();

        await Assert.That(response.Context.Single()).IsTypeOf<SessionMessageUser>();
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/context"));
    }

    [Test]
    public async Task GetContextAsync_Should_Return_An_Empty_List()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope("[]"));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").GetContextAsync();

        await Assert.That(response.Context).IsEmpty();
    }

    [Test]
    public async Task GetDiffAsync_Should_Return_The_Typed_Turn_Diffs()
    {
        var diff = new FixtureLoader().LoadJson("Serialization.known-diff-status.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope($"[{diff}]"));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").GetDiffAsync();

        await Assert.That(response.Diffs.Count).IsEqualTo(1);
        await Assert.That(response.Diffs[0].File).IsEqualTo("src/App.cs");
        await Assert.That(response.Diffs[0].Additions).IsEqualTo(1);
        await Assert.That(response.Diffs[0].Status).IsEqualTo(FileDiffInfoStatus.Modified);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/diff"));
    }

    [Test]
    public async Task GetDiffAsync_Should_Compose_The_From_To_And_Context_Query()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope("[]"));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").GetDiffAsync(new SessionDiffRequest
        {
            From = "msg_1",
            To = "msg_2",
            Context = 3,
        });

        await Assert.That(response.Diffs).IsEmpty();
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/diff?from=msg_1&to=msg_2&context=3"));
    }

    [Test]
    public async Task ListFormsAsync_Should_Return_The_Typed_Forms()
    {
        const string forms = "[{\"id\":\"frm_1\",\"sessionID\":\"ses_100\",\"title\":\"Pick a name\",\"fields\":[]},"
            + "{\"id\":\"frm_2\",\"sessionID\":\"ses_100\",\"title\":\"Approve deploy\",\"fields\":[]}]";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(forms));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").ListFormsAsync();

        await Assert.That(response.Forms.Count).IsEqualTo(2);
        await Assert.That(response.Forms[0].Id).IsEqualTo("frm_1");
        await Assert.That(response.Forms[0].Title).IsEqualTo("Pick a name");
        await Assert.That(response.Forms[1].Id).IsEqualTo("frm_2");
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/form"));
    }

    [Test]
    public async Task ListFormsAsync_Should_Return_An_Empty_List()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope("[]"));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").ListFormsAsync();

        await Assert.That(response.Forms).IsEmpty();
    }

    [Test]
    public async Task ListInboxAsync_Should_Return_The_Typed_Inbox_Items()
    {
        const string items = "[{\"id\":\"msg_1\",\"sessionID\":\"ses_100\",\"time\":{\"created\":1},\"type\":\"user\","
            + "\"payload\":{\"text\":\"hello\"},\"delivery\":\"queue\"},"
            + "{\"id\":\"msg_2\",\"sessionID\":\"ses_100\",\"time\":{\"created\":2},\"type\":\"compaction\","
            + "\"payload\":{},\"delivery\":\"steer\"}]";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(items));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").ListInboxAsync();

        await Assert.That(response.Inbox.Count).IsEqualTo(2);
        var user = (SessionInboxUser)response.Inbox[0];
        await Assert.That(user.Payload.Text).IsEqualTo("hello");
        await Assert.That(user.Delivery).IsEqualTo(SessionInboxDelivery.Queue);
        var compaction = (SessionInboxCompaction)response.Inbox[1];
        await Assert.That(compaction.Delivery).IsEqualTo(SessionInboxDelivery.Steer);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/inbox"));
    }

    [Test]
    public async Task ListInboxAsync_Should_Return_An_Empty_List()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope("[]"));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").ListInboxAsync();

        await Assert.That(response.Inbox).IsEmpty();
    }

    [Test]
    public async Task ListInstructionsEntryAsync_Should_Return_The_Typed_Entries()
    {
        const string entries = "[{\"key\":\"style\",\"value\":\"be terse\"},{\"key\":\"tone\",\"value\":\"friendly\"}]";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(entries));

        var response = await scenario.Client.Experimental.ListSessionInstructionsEntriesAsync("ses_100");

        await Assert.That(response.SessionInstructionsEntries.Count).IsEqualTo(2);
        await Assert.That(response.SessionInstructionsEntries[0].Key).IsEqualTo("style");
        await Assert.That(response.SessionInstructionsEntries[0].Value.GetString()).IsEqualTo("be terse");
        await Assert.That(response.SessionInstructionsEntries[1].Key).IsEqualTo("tone");
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/experimental/session/ses_100/instructions/entries"));
    }

    [Test]
    public async Task ListInstructionsEntryAsync_Should_Return_An_Empty_List()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope("[]"));

        var response = await scenario.Client.Experimental.ListSessionInstructionsEntriesAsync("ses_100");

        await Assert.That(response.SessionInstructionsEntries).IsEmpty();
    }

    [Test]
    public async Task ListRequestsAsync_Should_Return_The_Typed_Permission_Requests()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-permission-request.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope($"[{payload}]"));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").ListRequestsAsync();

        var request = response.Requests.Single();
        await Assert.That(request.Id).IsEqualTo("per_1");
        await Assert.That(request.Action).IsEqualTo("read");
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/permission"));
    }

    [Test]
    public async Task ListRequestsAsync_Should_Return_An_Empty_List()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope("[]"));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").ListRequestsAsync();

        await Assert.That(response.Requests).IsEmpty();
    }

    [Test]
    public async Task PutEnvironmentAsync_Should_Send_The_Typed_Body_On_The_204()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").SetEnvironmentAsync(new SessionEnvironmentRequest
        {
            Variables = new Dictionary<string, string>(StringComparer.Ordinal) { ["PATH"] = "/usr/bin", },
        });

        await Assert.That(response.Status).IsEqualTo(204);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/environment"));
        await Assert.That(request.Body).IsEqualTo("{\"variables\":{\"PATH\":\"/usr/bin\"}}");
    }

    [Test]
    public async Task CreateFormAsync_Should_Send_The_Typed_Body_And_Return_The_Typed_Form()
    {
        const string form = "{\"id\":\"frm_1\",\"sessionID\":\"ses_100\",\"title\":\"Pick a name\",\"fields\":[]}";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(form));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").CreateFormAsync(new SessionFormCreateRequest
        {
            Title = "Pick a name",
            Fields = [],
        });

        await Assert.That(response.Form.Id).IsEqualTo("frm_1");
        await Assert.That(response.Form.Title).IsEqualTo("Pick a name");
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/form"));
        await Assert.That(request.Body).IsEqualTo("{\"title\":\"Pick a name\",\"fields\":[]}");
    }

    [Test]
    public async Task GetFormAsync_Should_Return_The_Typed_Form()
    {
        var form = WireBodyData.FormDetail(WireBodyData.FormPendingState);
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(form));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").GetFormAsync("frm_1");

        await Assert.That(response.Form.Id).IsEqualTo("frm_1");
        await Assert.That(response.Form.Title).IsEqualTo("Approve deploy");
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/form/frm_1"));
    }

    [Test]
    public async Task GetFormAsync_Should_Return_The_Typed_Pending_State()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(WireBodyData.FormDetail(WireBodyData.FormPendingState)));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").GetFormAsync("frm_1");

        await Assert.That(response.Form.State).IsTypeOf<FormStatePending>();
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/form/frm_1"));
    }

    [Test]
    public async Task GetFormAsync_Should_Return_The_Typed_Answered_State()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(WireBodyData.FormDetail(WireBodyData.FormAnsweredState)));

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").GetFormAsync("frm_1");

        var answered = (FormStateAnswered)response.Form.State;
        await Assert.That(answered.Answer["q1"].Text).IsEqualTo("blue");
    }

    [Test]
    public async Task PostFormReplyAsync_Should_Send_The_Typed_Answer_On_The_204()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").ReplyToFormAsync("frm_1", new SessionFormReplyRequest
        {
            Answer = new Dictionary<string, FormValue>(StringComparer.Ordinal) { ["q1"] = FormValue.FromText("blue"), },
        });

        await Assert.That(response.Status).IsEqualTo(204);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/form/frm_1/reply"));
        await Assert.That(request.Body).IsEqualTo("{\"answer\":{\"q1\":\"blue\"}}");
    }

    [Test]
    public async Task PostFormReplyAsync_Should_Throw_The_Declared_400_Invalid_Answer()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.BadRequest,
            "{\"_tag\":\"FormInvalidAnswerError\",\"id\":\"frm_1\",\"message\":\"wrong shape\"}");

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Sessions.GetSessionClient("ses_100").ReplyToFormAsync("frm_1", new SessionFormReplyRequest
            {
                Answer = new Dictionary<string, FormValue>(StringComparer.Ordinal) { ["q1"] = FormValue.FromText("blue"), },
            }))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<FormInvalidAnswerError>();
    }

    [Test]
    public async Task PostFormReplyAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").ReplyToFormAsync(
            "frm_1",
            new SessionFormReplyRequest { Answer = new Dictionary<string, FormValue>(StringComparer.Ordinal), },
            OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task PostViewAsync_Should_Send_The_Typed_Body_On_The_204()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").MarkViewedAsync(new SessionViewRequest
        {
            Idle = 5,
        });

        await Assert.That(response.Status).IsEqualTo(204);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100/view"));
        await Assert.That(request.Body).IsEqualTo("{\"idle\":5}");
    }

    [Test]
    public async Task PostViewAsync_Should_Throw_The_Declared_404_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.SessionNotFoundError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Sessions.GetSessionClient("ses_9").MarkViewedAsync(new SessionViewRequest { Idle = 1, }))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<SessionNotFoundError>();
    }

    [Test]
    public async Task PostViewAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").MarkViewedAsync(
            new SessionViewRequest { Idle = 1, },
            OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task UpdateSessionAsync_Should_Send_The_Metadata_On_The_204()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);
        using var document = JsonDocument.Parse("{\"source\":\"patch\",\"attempt\":2}");
        var metadata = document.RootElement.EnumerateObject()
            .ToDictionary(static member => member.Name, static member => member.Value.Clone(), StringComparer.Ordinal);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").UpdateAsync(
            new SessionUpdateRequest
            {
                Metadata = new Optional<IReadOnlyDictionary<string, JsonElement>?>(metadata),
            });

        await Assert.That(response.Status).IsEqualTo(204);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method.Method).IsEqualTo("PATCH");
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100"));
        await Assert.That(request.Body).IsEqualTo("{\"metadata\":{\"source\":\"patch\",\"attempt\":2}}");
    }

    [Test]
    public async Task UpdateSessionAsync_Should_Send_The_Ruleset_On_The_204()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").UpdateAsync(
            new SessionUpdateRequest
            {
                Permissions = new Optional<IReadOnlyList<PermissionRule>?>([
                    new PermissionRule { Action = "run", Resource = "bash", Effect = PermissionEffect.Ask },
                    new PermissionRule { Action = "edit", Resource = "**/*.cs", Effect = PermissionEffect.Allow },
                ]),
            });

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method.Method).IsEqualTo("PATCH");
        await Assert.That(request.RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/session/ses_100"));
        await Assert.That(request.Body).IsEqualTo(
            "{\"permissions\":[{\"action\":\"run\",\"resource\":\"bash\",\"effect\":\"ask\"},"
            + "{\"action\":\"edit\",\"resource\":\"**/*.cs\",\"effect\":\"allow\"}]}");
    }

    [Test]
    public async Task UpdateSessionAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Sessions.GetSessionClient("ses_100").UpdateAsync(
                EmptyRuleset))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task UpdateSessionAsync_Should_Throw_The_Declared_404_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.SessionNotFoundError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Sessions.GetSessionClient("ses_9").UpdateAsync(
                EmptyRuleset))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<SessionNotFoundError>();
    }

    [Test]
    public async Task UpdateSessionAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Sessions.GetSessionClient("ses_100").UpdateAsync(
            EmptyRuleset,
            OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }
}
