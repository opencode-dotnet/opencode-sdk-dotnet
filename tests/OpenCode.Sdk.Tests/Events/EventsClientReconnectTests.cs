using System.Net;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Events;

/// <summary>
/// The event bus reconnects as the pinned client does. Each attempt has 2 seconds to answer and
/// deliver <c>server.connected</c> first. A connected stream is cut after 45 seconds without a
/// byte, and every failure of any kind is followed by a new attempt one second later. A stream
/// that has failed every attempt for one minute, from the drop or from the first attempt, throws
/// the last failure. Every bound runs on the test's clock, and each step waits until the bound it
/// passes is armed.
/// </summary>
public sealed class EventsClientReconnectTests
{
    private static readonly TimeSpan Delay = TimeSpan.FromSeconds(1);

    private static readonly TimeSpan ConnectBound = TimeSpan.FromSeconds(2);

    private static readonly TimeSpan IdleWindow = TimeSpan.FromSeconds(45);

    private static readonly TimeSpan OneTick = TimeSpan.FromTicks(1);

    private static readonly string ConnectedFrame = WireBodyData.Frames(ScriptedEventServer.Connected);

    [Test]
    public async Task ExecuteStreamAsync_Should_Carry_Per_Call_Location_On_Every_Reconnect_Attempt()
    {
        var time = new ManualTimeProvider();
        using var handler = new RecordingHttpHandler(static _ => ScriptedEventServer.StreamOf(ConnectedFrame, ends: true));
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, location: new LocationSelector { Directory = "/ambient" }, time: time);
        var options = new OpenCodeRequestOptions { Location = new LocationSelector { Directory = "/per-call" } };
        var stream = pipeline.ExecuteStreamAsync("event.subscribe", HttpMethod.Get, "/api/event",
            EventSubscribeResponseStreamAdapter.Instance, options, CancellationToken.None);
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
            .IsEquivalentTo(["%2Fper-call", "%2Fper-call", "%2Fper-call"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task A_Dropped_Stream_Should_Reconnect_After_One_Second_And_Announce_It_With_Server_Connected()
    {
        using var first = new LiveEventBody(WireBodyData.Frames(ScriptedEventServer.Connected, WireBodyData.SessionCreatedEvent), ends: true);
        using var second = new LiveEventBody(WireBodyData.Frames(ScriptedEventServer.Connected, WireBodyData.UnknownLogEvent));
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
        await using var events = server.Events.SubscribeAsync(caller.Token).GetAsyncEnumerator();
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
    public async Task A_Connected_Stream_Should_Be_Cut_After_45_Seconds_Without_A_Byte()
    {
        using var first = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(number =>
            number is 1 ? ScriptedEventServer.Stream(first) : ScriptedEventServer.StreamOf(ConnectedFrame));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        var next = PendingNext(events);
        await server.Time.WaitForArmedAsync(IdleWindow, HangGuard.Bound);
        server.Time.Advance(IdleWindow - OneTick);
        await Assert.That(next.IsCompleted).IsFalse();
        server.Time.Advance(OneTick);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        await Assert.That(server.RequestCount).IsEqualTo(1);
        server.Time.Advance(Delay);

        await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
        await Assert.That(server.RequestCount).IsEqualTo(2);
        await Assert.That(first.IsDisposed).IsTrue();
    }

    /// <summary>A client that sets its own idle window, as upstream's <c>idleTimeout</c> allows, is cut when that window passes and not one tick before.</summary>
    [Test]
    public async Task A_Configured_Idle_Window_Should_Cut_A_Silent_Stream_Exactly_When_It_Passes()
    {
        var window = TimeSpan.FromSeconds(10);
        using var first = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(
            number => number is 1 ? ScriptedEventServer.Stream(first) : ScriptedEventServer.StreamOf(ConnectedFrame),
            eventStreamIdleTimeout: window);
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        var next = PendingNext(events);
        await server.Time.WaitForArmedAsync(window, HangGuard.Bound);
        server.Time.Advance(window - OneTick);
        await Assert.That(next.IsCompleted).IsFalse();
        server.Time.Advance(OneTick);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        server.Time.Advance(Delay);

        await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
        await Assert.That(server.RequestCount).IsEqualTo(2);
        await Assert.That(first.IsDisposed).IsTrue();
    }

    [Test]
    public async Task A_Heartbeat_Should_Start_A_Configured_Idle_Window_Again()
    {
        var window = TimeSpan.FromSeconds(10);
        using var body = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Stream(body), eventStreamIdleTimeout: window);
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        var next = PendingNext(events);
        await server.Time.WaitForArmedAsync(window, HangGuard.Bound);
        server.Time.Advance(TimeSpan.FromSeconds(9));
        body.Append(ScriptedEventServer.Heartbeat);
        await server.Time.WaitForArmedAsync(window, HangGuard.Bound);
        server.Time.Advance(window - OneTick);
        await Assert.That(next.IsCompleted).IsFalse();
        body.Append(WireBodyData.Frames(WireBodyData.SessionCreatedEvent));

        await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<SessionCreated>();
        await Assert.That(server.RequestCount).IsEqualTo(1);
    }

    /// <summary>
    /// Before <c>server.connected</c> only the fixed 2 s connect bound runs: bytes that arrive first
    /// neither extend it nor hand the attempt to the idle window, even one shorter than the bound.
    /// </summary>
    [Test]
    public async Task Bytes_Before_Server_Connected_Should_Leave_The_Connect_Bound_In_Charge()
    {
        using var first = new LiveEventBody(ScriptedEventServer.Heartbeat);
        using var server = new ScriptedEventServer(
            number => number is 1 ? ScriptedEventServer.Stream(first) : ScriptedEventServer.StreamOf(ConnectedFrame),
            eventStreamIdleTimeout: TimeSpan.FromSeconds(1));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();

        var next = PendingNext(events);
        await server.Time.WaitForArmedAsync(ConnectBound, HangGuard.Bound);
        server.Time.Advance(TimeSpan.FromSeconds(1.5));
        first.Append(ScriptedEventServer.Heartbeat);
        server.Time.Advance(ConnectBound - TimeSpan.FromSeconds(1.5) - OneTick);
        await Assert.That(next.IsCompleted).IsFalse();
        await Assert.That(server.RequestCount).IsEqualTo(1);
        server.Time.Advance(OneTick);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        server.Time.Advance(Delay);

        await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
        await Assert.That(server.RequestCount).IsEqualTo(2);
        await Assert.That(first.IsDisposed).IsTrue();
    }

    /// <summary>Any byte counts as activity, even one that does not finish a line, as upstream's <c>onActivity</c> fires on every chunk.</summary>
    [Test]
    public async Task A_Single_Byte_Should_Start_The_Idle_Window_Again()
    {
        using var body = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Stream(body));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        var next = PendingNext(events);
        await server.Time.WaitForArmedAsync(IdleWindow, HangGuard.Bound);
        server.Time.Advance(TimeSpan.FromSeconds(30));
        body.Append(":");
        await server.Time.WaitForArmedAsync(IdleWindow, HangGuard.Bound);
        server.Time.Advance(IdleWindow - OneTick);
        await Assert.That(next.IsCompleted).IsFalse();
        body.Append(" heartbeat\n\n" + WireBodyData.Frames(WireBodyData.SessionCreatedEvent));

        await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<SessionCreated>();
        await Assert.That(server.RequestCount).IsEqualTo(1);
    }

    [Test]
    public async Task A_Heartbeat_Should_Keep_A_Quiet_Stream_Open()
    {
        using var body = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Stream(body));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        var next = PendingNext(events);
        await server.Time.WaitForArmedAsync(IdleWindow, HangGuard.Bound);
        server.Time.Advance(TimeSpan.FromSeconds(30));
        body.Append(ScriptedEventServer.Heartbeat);
        await server.Time.WaitForArmedAsync(IdleWindow, HangGuard.Bound);
        server.Time.Advance(IdleWindow - OneTick);
        await Assert.That(next.IsCompleted).IsFalse();
        body.Append(WireBodyData.Frames(WireBodyData.SessionCreatedEvent));

        await Assert.That(await next.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<SessionCreated>();
        await Assert.That(server.RequestCount).IsEqualTo(1);
    }

    /// <summary>The consumer's time with an event is not the server's silence, so a slow consumer is never cut.</summary>
    [Test]
    public async Task A_Consumer_Holding_An_Event_Should_Not_Count_As_Silence()
    {
        using var body = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Stream(body));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        server.Time.Advance(TimeSpan.FromMinutes(10));
        body.Append(WireBodyData.Frames(WireBodyData.SessionCreatedEvent));

        await Assert.That(await NextAsync(events)).IsTypeOf<SessionCreated>();
        await Assert.That(server.RequestCount).IsEqualTo(1);
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
    /// pinned client retries any failure: the minute decides when it stops, not the kind of failure.
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

    /// <summary>A wrong address or a refused credential surfaces after one minute of attempts, as the error the server answered.</summary>
    [Test]
    [Arguments(400, WireBodyData.InvalidRequestError, typeof(InvalidRequestError))]
    [Arguments(401, WireBodyData.UnauthorizedError, typeof(UnauthorizedError))]
    public async Task A_Stream_Refused_For_A_Minute_Should_Throw_The_Declared_Error_Exactly_At_The_Minute(int status, string body,
        Type expectedErrorType)
    {
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Status((HttpStatusCode)status, body));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var first = PendingNext(events);

        await FailEveryAttemptUntilTheMinuteAsync(server, first, cyclesBefore: 59);

        var exception = await Assert.That(first.WaitAsync(HangGuard.Bound)).Throws<OpenCodeApiException>();
        await Assert.That(exception!.Status).IsEqualTo(status);
        await Assert.That(exception.Error!.GetType()).IsEqualTo(expectedErrorType);
        await Assert.That(server.RequestCount).IsEqualTo(60);
    }

    /// <summary>Each failure replaces the one before, so the give-up throws the one that came last.</summary>
    [Test]
    public async Task The_Give_Up_Should_Throw_The_Latest_Failure()
    {
        using var server = new ScriptedEventServer(number => number % 2 is 1
            ? ScriptedEventServer.Status(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError)
            : ScriptedEventServer.Status(HttpStatusCode.InternalServerError, "{}"));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var first = PendingNext(events);

        await FailEveryAttemptUntilTheMinuteAsync(server, first, cyclesBefore: 59);

        var exception = await Assert.That(first.WaitAsync(HangGuard.Bound)).Throws<OpenCodeApiException>();
        await Assert.That(exception!.Status).IsEqualTo(500);
        await Assert.That(server.RequestCount).IsEqualTo(60);
    }

    /// <summary>An attempt still waiting at the minute is cut by the give-up, which throws the failure before it.</summary>
    [Test]
    public async Task The_Give_Up_Should_Cut_An_Attempt_In_Flight_And_Throw_The_Failure_Before_It()
    {
        using var server = new ScriptedEventServer(number => number switch
        {
            60 => ScriptedEventServer.Silence,
            59 => ScriptedEventServer.Status(HttpStatusCode.InternalServerError, "{}"),
            _ => ScriptedEventServer.Status(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError),
        });
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var first = PendingNext(events);
        for (var second = 0; second < 59; second++)
        {
            await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
            server.Time.Advance(Delay);
        }

        await server.WaitForRequestAsync(60, HangGuard.Bound);
        await server.Time.WaitForArmedAsync(ConnectBound, HangGuard.Bound);
        server.Time.Advance(Delay - OneTick);
        await Assert.That(first.IsCompleted).IsFalse();
        server.Time.Advance(OneTick);

        var exception = await Assert.That(first.WaitAsync(HangGuard.Bound)).Throws<OpenCodeApiException>();
        await Assert.That(exception!.Status).IsEqualTo(500);
        await Assert.That(server.AllResponsesReleased).IsTrue();
    }

    [Test]
    public async Task A_Stream_Failed_By_The_Server_For_A_Minute_Should_Throw_The_Typed_Failure_Cause()
    {
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.StreamOf(
            WireBodyData.NamedFrame("effect/http-api/stream/failure", WireBodyData.StreamFailureCause)));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var first = PendingNext(events);

        await FailEveryAttemptUntilTheMinuteAsync(server, first, cyclesBefore: 59);

        var exception = await Assert.That(first.WaitAsync(HangGuard.Bound)).Throws<OpenCodeStreamFailureException>();
        var cause = (StreamFailureCauseDie)exception!.Causes.Single();
        await Assert.That(cause.Defect.ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(cause.Defect.GetString()).IsEqualTo("boom");
    }

    /// <summary>Attempts that never connect run 2 seconds each plus the delay; the last one cut names the bound that cut it.</summary>
    [Test]
    public async Task A_Server_That_Never_Answers_Should_Throw_The_Connect_Bound_Exactly_At_The_Minute()
    {
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Silence);
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var first = PendingNext(events);

        for (var attempt = 1; attempt <= 20; attempt++)
        {
            await server.WaitForRequestAsync(attempt, HangGuard.Bound);
            await server.Time.WaitForArmedAsync(ConnectBound, HangGuard.Bound);
            server.Time.Advance(ConnectBound);
            await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound, count: attempt < 20 ? 1 : 2);
            if (attempt < 20)
            {
                server.Time.Advance(Delay);
            }
        }

        server.Time.Advance(Delay - OneTick);
        await Assert.That(first.IsCompleted).IsFalse();
        server.Time.Advance(OneTick);

        var exception = await Assert.That(first.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();
        await Assert.That(exception!.Message).Contains("did not connect within 2 seconds");
        await Assert.That(server.RequestCount).IsEqualTo(20);
    }

    /// <summary>A connection resets the minute; the next minute runs from the moment that connection drops.</summary>
    [Test]
    public async Task A_Connection_Should_Restart_The_Minute_From_Its_Drop()
    {
        using var connection = new LiveEventBody(WireBodyData.Frames(ScriptedEventServer.Connected));
        using var server = new ScriptedEventServer(number => number is 60
            ? ScriptedEventServer.Stream(connection)
            : ScriptedEventServer.Status(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var first = PendingNext(events);
        for (var second = 0; second < 59; second++)
        {
            await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
            server.Time.Advance(Delay);
        }

        await Assert.That(await first.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();
        server.Time.Advance(TimeSpan.FromSeconds(5));
        connection.Append(WireBodyData.Frames(WireBodyData.SessionCreatedEvent));
        await Assert.That(await NextAsync(events)).IsTypeOf<SessionCreated>();

        var next = PendingNext(events);
        connection.End();
        await FailEveryAttemptUntilTheMinuteAsync(server, next, cyclesBefore: 59);

        _ = await Assert.That(next.WaitAsync(HangGuard.Bound)).Throws<OpenCodeApiException>();
        await Assert.That(server.RequestCount).IsEqualTo(119);
    }

    [Test]
    public async Task The_Callers_Token_Should_End_The_Stream_During_The_Delay_Without_Another_Attempt()
    {
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Status(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError));
        using var caller = new CancellationTokenSource();
        await using var events = server.Events.SubscribeAsync(caller.Token).GetAsyncEnumerator();

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
        await using var events = server.Events.SubscribeAsync(caller.Token).GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();

        var next = PendingCancellationAsync(events);
        await caller.CancelOnWorkerAsync();

        var exception = await next.WaitAsync(HangGuard.Bound);
        await Assert.That(exception).IsNotNull();
        await Assert.That(exception!.CancellationToken).IsEqualTo(caller.Token);
        server.Time.Advance(TimeSpan.FromMinutes(2));
        await Assert.That(server.RequestCount).IsEqualTo(1);
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

    /// <summary>
    /// Fails <paramref name="cyclesBefore"/> one-second cycles, then shows the last delay ends in the
    /// give-up exactly at the minute and not one tick before. Every attempt here fails at once. The
    /// clock runs timers due at the same instant in the order they were created, so the give-up,
    /// created with the stream, runs before the last delay.
    /// </summary>
    private static async Task FailEveryAttemptUntilTheMinuteAsync(ScriptedEventServer server, Task<bool> pending, int cyclesBefore)
    {
        for (var second = 0; second < cyclesBefore; second++)
        {
            await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
            server.Time.Advance(Delay);
        }

        // The give-up and the last delay fall due at the same instant; both are armed before time moves.
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound, count: 2);
        server.Time.Advance(Delay - OneTick);
        await Assert.That(pending.IsCompleted).IsFalse();
        server.Time.Advance(OneTick);
    }

    private static HttpResponseMessage FailedOpen(string failure) => failure switch
    {
        "unauthorized" => ScriptedEventServer.Status(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError),
        "server-error" => ScriptedEventServer.Status(HttpStatusCode.InternalServerError, "{}"),
        "undeclared-success" => ScriptedEventServer.Status(HttpStatusCode.Created, "{}"),
        "not-an-event-stream" => ScriptedEventServer.Status(HttpStatusCode.OK, "{}"),
        "first-event-not-server-connected" => ScriptedEventServer.StreamOf(WireBodyData.Frames(WireBodyData.SessionCreatedEvent)),
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

    /// <summary>
    /// Asks for the next event without waiting for it, and keeps the cancellation it ends with whole:
    /// a downlevel <c>WaitAsync</c> rethrows a cancellation without its token.
    /// </summary>
    private static async Task<OperationCanceledException?> PendingCancellationAsync(IAsyncEnumerator<IEvent> events)
    {
        try
        {
            _ = await events.MoveNextAsync();
            return null;
        }
        catch (OperationCanceledException exception)
        {
            return exception;
        }
    }

    /// <summary>Asks for the next event without waiting for it, so the test can move time while the read is pending.</summary>
    private static Task<bool> PendingNext(IAsyncEnumerator<IEvent> events) => events.MoveNextAsync().AsTask();

    private static async Task<IEvent> NextAsync(IAsyncEnumerator<IEvent> events)
    {
        if (!await events.MoveNextAsync().AsTask().WaitAsync(HangGuard.Bound))
        {
            throw new InvalidOperationException("The event stream ended.");
        }

        return events.Current;
    }
}
