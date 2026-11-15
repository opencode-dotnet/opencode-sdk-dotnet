using System.Net;
using System.Reflection;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tests.Events.Support.EventStreamSteps;

namespace OpenCode.Sdk.Tests.Events;

/// <summary>
/// The event bus reconnects as the pinned client does. Each attempt has 2 seconds to answer and
/// deliver <c>server.connected</c> first. Every failure of any kind is followed by a new attempt
/// one second later. Attempt failures are classified on connection state while the source keeps
/// retrying. Every bound runs on the test's clock, and each step
/// waits until the bound it passes is armed.
/// </summary>
public sealed class EventsClientReconnectTests
{
    [Test]
    public async Task SubscribeAsync_Should_Decode_Unselected_Frames_And_Reconnect_All_Subscribers_On_Invalid_Json()
    {
        using var initial = new LiveEventBody(ConnectedFrame);
        using var replacement = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(index => ScriptedEventServer.Stream(index == 1 ? initial : replacement));
        var request = new EventSubscribeRequest { Types = [new("rpc.sdk-test")] };
        await using var first = server.Events.SubscribeAsync(request).GetAsyncEnumerator();
        await using var second = server.Events.SubscribeAsync(request).GetAsyncEnumerator();
        var oldMarker = await NextAsync(first);
        await Assert.That(await NextAsync(second)).IsSameReferenceAs(oldMarker);
        await Assert.That(server.RequestCount).IsEqualTo(1);
        Drop(initial, "malformed-frame");
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        server.Time.Advance(Delay);
        var newMarker = await NextAsync(first);
        await Assert.That(newMarker).IsNotSameReferenceAs(oldMarker);
        await Assert.That(await NextAsync(second)).IsSameReferenceAs(newMarker);
        var drifted = System.Text.Json.Nodes.JsonNode.Parse(ScriptedEventServer.Connected)!.AsObject();
        drifted["data"] = false;
        replacement.Append(WireBodyData.Frames(drifted.ToJsonString()));
        var left = await NextAsync(first);
        var right = await NextAsync(second);

        await Assert.That(left).IsTypeOf<UnknownEvent>();
        await Assert.That(left.Type).IsEqualTo("server.connected");
        await Assert.That(right).IsSameReferenceAs(left);
        await Assert.That(server.RequestCount).IsEqualTo(2);
        await Assert.That(server.EarlierResponsesReleased).IsEquivalentTo([true, true], CollectionOrdering.Matching);
    }

    private readonly FixtureLoader _fixtures = new();

    [Test]
    [Arguments("door")]
    [Arguments("spine")]
    public async Task SubscribeAsync_Should_Send_Only_The_Ambient_Location_Or_Refuse_The_Raw_Spine(string entry)
    {
        var time = new ManualTimeProvider();
        using var handler = CreateReconnectHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, location: new LocationSelector { Directory = "/ambient" }, time: time);
        var options = new OpenCodeRequestOptions { Location = new LocationSelector { Directory = "/override/ç" } };
        if (string.Equals(entry, "spine", StringComparison.Ordinal))
        {
            var refusal = await Assert.That(() => Subscribe(entry, pipeline, options)).Throws<InvalidOperationException>();
            await Assert.That(refusal!.Message).IsEqualTo("Operation 'event.subscribe' must be opened through its shared event feed.");
            await Assert.That(handler.Requests).IsEmpty();
            return;
        }

        var stream = Subscribe(entry, pipeline, options);
        await Assert.That(handler.Requests).IsEmpty().Because(entry);
        await using var events = stream.GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        for (var attempt = 2; attempt <= 3; attempt++)
        {
            var next = PendingNext(events);
            await time.WaitForArmedAsync(Delay, HangGuard.Bound);
            time.Advance(Delay);
            await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
            await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
            await Assert.That(handler.Requests.Count).IsEqualTo(attempt);
        }

        await Assert.That(handler.Requests.Select(static request => request.Headers["x-opencode-directory"]))
            .IsEquivalentTo(["%2Fambient", "%2Fambient", "%2Fambient"], CollectionOrdering.Matching);
        await Assert.That(options.Location.Directory).IsEqualTo("/override/ç");
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task SubscribeAsync_Should_Refuse_Per_Call_Options_Before_Enumeration(int kind)
    {
        using var scenario = ContractScenario.Responding();
        var options = kind switch
        {
            0 => OpenCodeRequestOptions.NoThrow,
            1 => new OpenCodeRequestOptions { ErrorBehavior = (ErrorBehavior)42 },
            _ => new OpenCodeRequestOptions { NetworkTimeout = kind == 2 ? Delay : Timeout.InfiniteTimeSpan },
        };

        var exception = await Assert.That(() => scenario.Client.Events.SubscribeAsync(requestOptions: options)).Throws<ArgumentException>();

        await Assert.That(exception!.GetType()).IsEqualTo(kind == 1 ? typeof(ArgumentOutOfRangeException) : typeof(ArgumentException));
        await Assert.That(exception.ParamName).IsEqualTo("requestOptions");
        if (kind == 0)
        {
            await Assert.That(exception.Message).StartsWith(
                "Event subscriptions report ordinary attempt failures through connection state; NoThrow is not supported.");
        }

        if (exception is ArgumentOutOfRangeException range)
        {
            await Assert.That(range.ActualValue).IsEqualTo(options.ErrorBehavior);
        }

        await Assert.That(scenario.Requests).IsEmpty();
    }

    [Test]
    public async Task A_Dropped_Stream_Should_Reconnect_After_One_Second_And_Announce_It_With_Server_Connected()
    {
        using var first = new LiveEventBody(
            WireBodyData.Frames(ScriptedEventServer.Connected, _fixtures.LoadJson("Events.session-created.json")), ends: true);
        using var second = new LiveEventBody(WireBodyData.Frames(ScriptedEventServer.Connected, _fixtures.LoadJson("Events.unknown-log-event.json")));
        using var server = new ScriptedEventServer(number => ScriptedEventServer.Stream(number is 1 ? first : second));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();
        await Assert.That(await NextAsync(events)).IsTypeOf<SessionCreated>();

        var reconnect = PendingNext(events);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        await Assert.That(server.RequestCount).IsEqualTo(1);
        server.Time.Advance(Delay);

        await Assert.That(await reconnect.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
        await Assert.That(await NextAsync(events)).IsTypeOf<UnknownEvent>();
        await Assert.That(server.RequestCount).IsEqualTo(2);
        await Assert.That(server.EarlierResponsesReleased[1]).IsTrue();
        await Assert.That(first.IsDisposed).IsTrue();
    }

    /// <summary>Leaving the loop releases the connection, and nothing opens another.</summary>
    [Test]
    public async Task Leaving_The_Loop_Should_Release_The_Connection_And_Open_No_Other()
    {
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.StreamOf(ConnectedFrame));
        await using (var events = server.Events.SubscribeAsync().GetAsyncEnumerator())
        {
            await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();
        }

        await ObserveFeedStopAsync(server.Events);
        await Assert.That(server.AllResponsesReleased).IsTrue();
        server.Time.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(server.RequestCount).IsEqualTo(1);
    }

    /// <summary>The client's network timeout bounds one-shot calls; the bus keeps its own windows.</summary>
    [Test]
    public async Task The_Clients_Network_Timeout_Should_Not_Reach_The_Bus()
    {
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.StreamOf(ConnectedFrame), networkTimeout: TimeSpan.FromSeconds(1));
        using var caller = new CancellationTokenSource();
        await using var events = server.Events.SubscribeAsync(cancellationToken: caller.Token).GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        var next = PendingNext(events);
        await server.Time.WaitForArmedAsync(IdleWindow, HangGuard.Bound);
        server.Time.Advance(IdleWindow - OneTick);

        await Assert.That(next.IsCompleted).IsFalse();
        await Assert.That(server.RequestCount).IsEqualTo(1);
        await caller.CancelOnWorkerAsync();
        _ = await Assert.That(next.WaitAsync(HangGuard.Bound)).Throws<OperationCanceledException>();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task An_Attempt_Should_Be_Cut_When_It_Does_Not_Connect_Within_2_Seconds(bool answersHeaders)
    {
        using var server = new ScriptedEventServer(number => number switch
        {
            1 when answersHeaders => ScriptedEventServer.StreamOf(string.Empty),
            1 => ScriptedEventServer.Silence,
            _ => ScriptedEventServer.StreamOf(ConnectedFrame),
        });
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();

        var first = PendingNext(events);
        await server.WaitForRequestAsync(1, HangGuard.Bound);
        server.Time.Advance(ConnectBound - OneTick);
        await Assert.That(first.IsCompleted).IsFalse();
        server.Time.Advance(OneTick);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        server.Time.Advance(Delay);

        await Assert.That(await first.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
        await Assert.That(server.RequestCount).IsEqualTo(2);
    }

    /// <summary>
    /// Every way an open can fail is followed by a new attempt, the deterministic ones too, as the
    /// pinned client retries any failure rather than selecting a permanent-failure category.
    /// </summary>
    [Test]
    [Arguments("unauthorized")]
    [Arguments("server-error")]
    [Arguments("undeclared-success")]
    [Arguments("not-an-event-stream")]
    [Arguments("first-event-not-server-connected")]
    [Arguments("failure-frame-first")]
    [Arguments("malformed-frame-first")]
    [Arguments("ends-before-any-event")]
    public async Task Every_Failed_Open_Should_Be_Followed_By_A_New_Attempt(string failure)
    {
        using var server = new ScriptedEventServer(number =>
            number is 1 ? FailedOpen(failure) : ScriptedEventServer.StreamOf(ConnectedFrame));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();

        var first = PendingNext(events);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        await Assert.That(server.RequestCount).IsEqualTo(1);
        server.Time.Advance(Delay);

        await Assert.That(await first.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
        await Assert.That(server.RequestCount).IsEqualTo(2);
    }

    /// <summary>A rejected first event is never delivered; only the next connection's announcement reaches the consumer.</summary>
    [Test]
    public async Task A_First_Event_That_Is_Not_Server_Connected_Should_Not_Be_Delivered()
    {
        using var server = new ScriptedEventServer(number => ScriptedEventServer.StreamOf(
            number is 1 ? WireBodyData.Frames(_fixtures.LoadJson("Events.session-created.json")) : ConnectedFrame, ends: true));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var received = new List<Type>();
        var reading = ReadThroughConnectionAsync();
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        server.Time.Advance(Delay);
        await reading.WaitAsync(HangGuard.Bound);

        await Assert.That(received).IsEquivalentTo([typeof(EventServerConnected)], CollectionOrdering.Matching);

        async Task ReadThroughConnectionAsync()
        {
            while (await events.MoveNextAsync())
            {
                received.Add(events.Current.GetType());
                if (events.Current is EventServerConnected)
                {
                    return;
                }
            }
        }
    }

    [Test]
    [Arguments("connection-dropped")]
    [Arguments("server-ended-the-stream")]
    [Arguments("failure-frame")]
    [Arguments("malformed-frame")]
    public async Task Every_Drop_Of_A_Connected_Stream_Should_Be_Followed_By_A_New_Attempt(string drop)
    {
        using var first = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(number =>
            number is 1 ? ScriptedEventServer.Stream(first) : ScriptedEventServer.StreamOf(ConnectedFrame));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        var next = PendingNext(events);
        Drop(first, drop);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        server.Time.Advance(Delay);

        await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
        await Assert.That(server.RequestCount).IsEqualTo(2);
    }

    /// <summary>An opening status refusal retains its classified error even when the connect window passed during the send.</summary>
    [Test]
    public async Task An_Opening_Status_Refusal_Should_Keep_Its_Classified_Error_When_The_Connect_Window_Passes()
    {
        var time = new ManualTimeProvider();
        await AssertOpeningTransportFailureMessageAsync(time, _ =>
        {
            // The synchronous responder returns after the already-armed connect window passed.
            time.Advance(TimeSpan.FromMinutes(1));
            return new HttpResponseMessage(HttpStatusCode.Redirect);
        }, "The opencode API returned undeclared redirect status 302.");
    }

    [Test]
    public async Task The_Callers_Token_Should_End_The_Stream_During_The_Delay_Without_Another_Attempt()
    {
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Status(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError));
        using var caller = new CancellationTokenSource();
        await using var events = server.Events.SubscribeAsync(cancellationToken: caller.Token).GetAsyncEnumerator();

        var first = PendingCancellationAsync(events);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        await caller.CancelOnWorkerAsync();

        var exception = await first.WaitAsync(HangGuard.Bound);
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.CancellationToken).IsEqualTo(caller.Token);
        server.Time.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(server.RequestCount).IsEqualTo(1);
    }

    [Test]
    public async Task The_Callers_Token_Should_End_A_Connected_Stream_Without_Another_Attempt()
    {
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.StreamOf(ConnectedFrame));
        using var caller = new CancellationTokenSource();
        await using var events = await SubscribeConnectedAsync(server, caller.Token);

        var next = PendingCancellationAsync(events);
        await caller.CancelOnWorkerAsync();

        var exception = await next.WaitAsync(HangGuard.Bound);
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.CancellationToken).IsEqualTo(caller.Token);
        server.Time.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(server.RequestCount).IsEqualTo(1);
    }

    /// <summary>Cancellation already observed while holding an event wins over subsequent client disposal.</summary>
    [Test]
    public async Task The_Callers_Cancellation_Should_Win_Over_Disposal_While_Holding_An_Event()
    {
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.StreamOf(ConnectedFrame));
        using var caller = new CancellationTokenSource();
        await using var events = await SubscribeConnectedAsync(server, caller.Token);
        await caller.CancelOnWorkerAsync();
        server.DisposeClient();

        await AssertReadCanceledByCallerAsync(PendingCancellationAsync(events), caller.Token);
    }

    /// <summary>A disposed client ends the stream at once, whatever it is doing, and nothing opens again.</summary>
    [Test]
    [Arguments("awaiting-headers")]
    [Arguments("reading")]
    [Arguments("holding-an-event")]
    [Arguments("waiting-to-reconnect")]
    public async Task A_Disposed_Client_Should_End_The_Stream_At_Once(string phase)
    {
        using var server = new ScriptedEventServer(_ => phase switch
        {
            "awaiting-headers" => ScriptedEventServer.Silence,
            "waiting-to-reconnect" => ScriptedEventServer.Status(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError),
            _ => ScriptedEventServer.StreamOf(ConnectedFrame),
        });
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        Task<bool> next;
        switch (phase)
        {
            case "awaiting-headers":
                next = PendingNext(events);
                await server.WaitForRequestAsync(1, HangGuard.Bound);
                server.DisposeClient();
                break;
            case "reading":
                await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();
                next = PendingNext(events);
                await server.Time.WaitForArmedAsync(IdleWindow, HangGuard.Bound);
                server.DisposeClient();
                break;
            case "holding-an-event":
                await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();
                server.DisposeClient();
                next = PendingNext(events);
                break;
            default:
                next = PendingNext(events);
                await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
                server.DisposeClient();
                break;
        }

        _ = await Assert.That(next.WaitAsync(HangGuard.Bound)).Throws<ObjectDisposedException>();
        server.Time.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(server.RequestCount).IsEqualTo(1);
        await Assert.That(server.AllResponsesReleased).IsTrue();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SubscribeAsync_Should_Release_The_Response_While_The_Consumer_Holds_An_Event(bool disposeClient)
    {
        using var body = new LiveEventBody(ConnectedFrame);
        var initiallyReading = body.NextEmptyRead;
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Stream(body));
        using var caller = new CancellationTokenSource();
        await using var events = await SubscribeConnectedAsync(server, caller.Token);
        await initiallyReading.WaitAsync(HangGuard.Bound);
        var interval = TimeSpan.FromTicks(IdleWindow.Ticks / 2);
        for (var elapsed = TimeSpan.Zero; elapsed < TimeSpan.FromMinutes(10); elapsed += interval)
        {
            server.Time.Advance(interval);
            var reading = body.NextEmptyRead;
            body.Append(ScriptedEventServer.Heartbeat);
            await reading.WaitAsync(HangGuard.Bound);
        }

        await Assert.That(body.IsDisposed).IsFalse();

        if (disposeClient)
        {
            server.DisposeClient();
        }
        else
        {
            await caller.CancelOnWorkerAsync();
        }

        // Assert release while still suspended at the yield, before requesting another event.
        await ObserveFeedStopAsync(server.Events);
        await Assert.That(server.AllResponsesReleased).IsTrue();
        await Assert.That(body.IsDisposed).IsTrue();
        if (disposeClient)
        {
            var exception = await Assert.That(async () => _ = await events.MoveNextAsync()).Throws<ObjectDisposedException>();
            await Assert.That(exception!.ObjectName).IsEqualTo(typeof(OpenCodeClient).FullName);
        }
        else
        {
            var exception = await PendingCancellationAsync(events).WaitAsync(HangGuard.Bound);
            await Assert.That(exception).IsNotNull();
            await Assert.That(exception!.CancellationToken).IsEqualTo(caller.Token);
        }

        server.Time.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(server.RequestCount).IsEqualTo(1);
    }

    private static async Task ObserveFeedStopAsync(EventsClient events)
    {
        var feed = (SharedEventFeed)typeof(EventsClient).GetField("_feed", BindingFlags.NonPublic | BindingFlags.Instance)!.GetValue(events)!;
        await feed.JoinAsync().WaitAsync(HangGuard.Bound);
    }

    /// <summary>Asserts the pull ends in the caller's own cancellation with the stream's canceled text.</summary>
    private static async Task AssertReadCanceledByCallerAsync(Task<OperationCanceledException?> pending, CancellationToken caller)
    {
        // The caller's token is already canceled: it is the identity under test, so the wait must not observe it.
        var failure = await Assert.That(pending.WaitAsync(HangGuard.Bound, CancellationToken.None)).ThrowsNothing();
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.CancellationToken).IsEqualTo(caller);
        await Assert.That(failure.Message).IsEqualTo("The opencode event stream read was canceled.");
    }

    /// <summary>Checks the opening failure's message while the subscription and its transport are still owned.</summary>
    private static async Task AssertOpeningTransportFailureMessageAsync(ManualTimeProvider time,
        Func<HttpRequestMessage, HttpResponseMessage> answer, string expectedMessage)
    {
        var answered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var handler = new RecordingHttpHandler(request =>
        {
            try
            {
                return answer(request);
            }
            finally
            {
                answered.SetResult();
            }
        });
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, time: time);
        var door = new EventsClient(pipeline);
        await using var events = door.SubscribeAsync().GetAsyncEnumerator();
        var moving = PendingNext(events);
        await answered.Task.WaitAsync(HangGuard.Bound);
        await time.WaitForArmedAsync(Delay, HangGuard.Bound);
        var state = door.GetConnectionState();
        await Assert.That(state.Error).IsTypeOf<OpenCodeTransportException>();
        await Assert.That(state.Error!.Message).IsEqualTo(expectedMessage);
        await events.DisposeAsync();
        await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsFalse();
    }

    private static async Task<IAsyncEnumerator<IEvent>> SubscribeConnectedAsync(ScriptedEventServer server, CancellationToken caller)
    {
        // The subscription already carries the caller's token; enumeration adds none.
        var events = server.Events.SubscribeAsync(cancellationToken: caller).GetAsyncEnumerator(CancellationToken.None);
        var handedOff = false;
        try
        {
            await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();
            handedOff = true;
            return events;
        }
        finally
        {
            if (!handedOff)
            {
                await events.DisposeAsync();
            }
        }
    }

    private HttpResponseMessage FailedOpen(string failure) => failure switch
    {
        "unauthorized" => ScriptedEventServer.Status(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError),
        "server-error" => ScriptedEventServer.Status(HttpStatusCode.InternalServerError, "{}"),
        "undeclared-success" => ScriptedEventServer.Status(HttpStatusCode.Created, "{}"),
        "not-an-event-stream" => ScriptedEventServer.Status(HttpStatusCode.OK, "{}"),
        "first-event-not-server-connected" => ScriptedEventServer.StreamOf(WireBodyData.Frames(_fixtures.LoadJson("Events.session-created.json"))),
        "failure-frame-first" => ScriptedEventServer.StreamOf(
            WireBodyData.NamedFrame("effect/http-api/stream/failure", WireBodyData.StreamFailureCause)),
        "malformed-frame-first" => ScriptedEventServer.StreamOf("data: {not json\n\n"),
        "ends-before-any-event" => ScriptedEventServer.StreamOf(string.Empty, ends: true),
        _ => throw new ArgumentOutOfRangeException(nameof(failure), failure, "Unknown failure."),
    };

    private static void Drop(LiveEventBody body, string drop)
    {
        switch (drop)
        {
            case "connection-dropped":
                body.Fail();
                break;
            case "server-ended-the-stream":
                body.End();
                break;
            case "failure-frame":
                body.Append(WireBodyData.NamedFrame("effect/http-api/stream/failure", WireBodyData.StreamFailureCause));
                break;
            case "malformed-frame":
                body.Append("data: {not json\n\n");
                break;
            default:
                throw new ArgumentOutOfRangeException(nameof(drop), drop, "Unknown drop.");
        }
    }

    /// <summary>Exercises the public door or the raw stream spine's explicit refusal.</summary>
    private static IAsyncEnumerable<IEvent> Subscribe(string entry, Pipeline pipeline, OpenCodeRequestOptions options) => entry == "door"
        ? new EventsClient(pipeline).SubscribeAsync(requestOptions: options)
        : pipeline.ExecuteStreamAsync("event.subscribe", HttpMethod.Get, "/api/event", EventSubscribeResponseStreamAdapter.Instance, options, CancellationToken.None);

    [Test]
    public async Task The_Callers_Cancellation_Should_Win_Over_Disposal_During_The_Delay()
    {
        using var server = new ScriptedEventServer(static _ => throw new HttpRequestException());
        using var caller = new CancellationTokenSource();
        await using var events = server.Events.SubscribeAsync(cancellationToken: caller.Token).GetAsyncEnumerator();

        var pending = PendingCancellationAsync(events);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        await caller.CancelOnWorkerAsync();
        server.DisposeClient();

        await AssertReadCanceledByCallerAsync(pending, caller.Token);
    }

    [Test]
    public async Task A_First_Event_Refusal_Should_Report_The_Exact_First_Frame_Text()
    {
        using var server = new ScriptedEventServer(index => ScriptedEventServer.StreamOf(index == 1
            ? WireBodyData.Frames(_fixtures.LoadJson("Events.session-created.json"))
            : ConnectedFrame));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var first = PendingNext(events);

        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        var state = server.Events.GetConnectionState();
        await Assert.That(state.Error).IsTypeOf<OpenCodeTransportException>();
        await Assert.That(state.Error!.Message).IsEqualTo("The opencode event stream did not start with server.connected.");
        server.Time.Advance(Delay);
        await Assert.That(await first.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current.Type).IsEqualTo("server.connected");
    }
}
