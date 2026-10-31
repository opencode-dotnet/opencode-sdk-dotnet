using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;
using static OpenCode.Sdk.Tests.Events.Support.EventStreamSteps;

namespace OpenCode.Sdk.Tests.Events;

/// <summary>
/// The event bus reconnects as the pinned client does. Each attempt has 2 seconds to answer and
/// deliver <c>server.connected</c> first. Every failure of any kind is followed by a new attempt
/// one second later. A stream that has failed every attempt for one minute, from the drop or from
/// the first attempt, throws the last failure. Every bound runs on the test's clock, and each step
/// waits until the bound it passes is armed.
/// </summary>
public sealed class EventsClientReconnectTests
{
    [Test]
    public async Task SubscribeAsync_Should_Send_Types_On_Every_Reconnect_Attempt()
    {
        var time = new ManualTimeProvider();
        using var handler = CreateReconnectHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, time: time);
        var request = new EventSubscribeRequest
        {
            Types = [new EventSubscribeRequestTypes(EventSubscribeRequestTypes.Values.SessionCreated), new EventSubscribeRequestTypes("rpc.sdk-test")],
        };
        await using var events = new EventsClient(pipeline).SubscribeAsync(request).GetAsyncEnumerator();

        var reconnect = await PullAfterConnectionAsync(events);
        await time.WaitForArmedAsync(Delay, HangGuard.Bound);
        time.Advance(Delay);
        await Assert.That(await reconnect.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current).IsTypeOf<EventServerConnected>();

        await Assert.That(handler.Requests.Count).IsEqualTo(2);
        await Assert.That(handler.Requests.Select(static attempt => attempt.RequestUri!.Query))
            .IsEquivalentTo(["?types=session.created&types=rpc.sdk-test", "?types=session.created&types=rpc.sdk-test"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task SubscribeAsync_Should_Omit_The_Query_Without_Types(int kind)
    {
        using var scenario = ContractScenario.RespondingWithFrames(ConnectedFrame);
        var request = kind switch
        {
            0 => null,
            1 => new EventSubscribeRequest(),
            _ => new EventSubscribeRequest { Types = [] },
        };
        await using var events = scenario.Client.Events.SubscribeAsync(request).GetAsyncEnumerator();

        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();
        await Assert.That(scenario.Requests.Single().RequestUri!.Query).IsEqualTo(string.Empty);
    }

    [Test]
    [Arguments("door")]
    [Arguments("spine")]
    public async Task Per_Call_Location_Should_Be_Sent_On_Every_Reconnect_Attempt(string entry)
    {
        var time = new ManualTimeProvider();
        using var handler = CreateReconnectHandler();
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, location: new LocationSelector { Directory = "/ambient" }, time: time);
        var stream = Subscribe(entry, pipeline, new OpenCodeRequestOptions { Location = new LocationSelector { Directory = "/override/ç" } });
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
            .IsEquivalentTo(["%2Foverride%2F%C3%A7", "%2Foverride%2F%C3%A7", "%2Foverride%2F%C3%A7"], CollectionOrdering.Matching);
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
        if (exception is ArgumentOutOfRangeException range)
        {
            await Assert.That(range.ActualValue).IsEqualTo(options.ErrorBehavior);
        }

        await Assert.That(scenario.Requests).IsEmpty();
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

    /// <summary>A rejected first event is never delivered; only the next connection's announcement reaches the consumer.</summary>
    [Test]
    public async Task A_First_Event_That_Is_Not_Server_Connected_Should_Not_Be_Delivered()
    {
        using var server = new ScriptedEventServer(number => ScriptedEventServer.StreamOf(
            number is 1 ? WireBodyData.Frames(WireBodyData.SessionCreatedEvent) : ConnectedFrame, ends: true));
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

    /// <summary>A clean server end remains the last failure when the drop's give-up minute passes.</summary>
    [Test]
    public async Task An_Ended_Stream_Should_Report_The_Ended_Text_At_Give_Up()
    {
        using var server = new ScriptedEventServer(number =>
            ScriptedEventServer.StreamOf(number is 1 ? ConnectedFrame : string.Empty, ends: true));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var next = await PullAfterConnectionAsync(events);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        server.Time.Advance(TimeSpan.FromMinutes(1));

        var failure = await Assert.That(next.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).IsEqualTo("The opencode event stream ended.");
    }

    /// <summary>A response rejected as the give-up passes retains its own protocol failure.</summary>
    [Test]
    public async Task The_Give_Up_Should_Throw_An_Attempts_Own_Failure_When_Its_Response_Arrives_At_The_Minute()
    {
        var time = new ManualTimeProvider();
        await AssertOpeningTransportFailureMessageAsync(time, _ =>
        {
            // The send has started, so both the give-up and connect windows are armed.
            time.Advance(TimeSpan.FromMinutes(1));
            return new HttpResponseMessage(HttpStatusCode.Redirect);
        }, "The opencode API returned undeclared redirect status 302.");
    }

    /// <summary>A first attempt cut by give-up, before any failure was recorded, names the give-up minute.</summary>
    [Test]
    public async Task The_Give_Up_Should_Name_Its_Minute_When_It_Cuts_The_First_Attempt()
    {
        var time = new ManualTimeProvider();
        await AssertOpeningTransportFailureMessageAsync(time, _ =>
        {
            // Keep the send on this stack until both windows have passed, before reporting its cancellation.
            time.Advance(TimeSpan.FromMinutes(1));
            throw new OperationCanceledException();
        }, "The opencode event stream did not connect within 60 seconds.");
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
        await Assert.That(exception!.Message).IsEqualTo("The opencode server did not answer the event stream request within 2 seconds.");
        await Assert.That(server.RequestCount).IsEqualTo(20);
    }

    /// <summary>An answered attempt that sends no event names that silence when its last reconnect delay reaches the give-up.</summary>
    [Test]
    public async Task A_Server_That_Answers_Without_An_Event_Should_Throw_The_Connect_Bound_At_Give_Up()
    {
        using var body = new BlockingContent();
        body.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        using var server = new ScriptedEventServer(number => number < 58
            ? ScriptedEventServer.Status(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError)
            : new HttpResponseMessage(HttpStatusCode.OK) { Content = body });
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var first = PendingNext(events);

        // The final attempt starts at 57 s: its connect bound ends at 59 s, then the delay
        // and give-up fall due together. Wait for its body read so the headers are known to be in.
        for (var second = 0; second < 57; second++)
        {
            await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
            server.Time.Advance(Delay);
        }

        await body.ReadStarted.WaitAsync(HangGuard.Bound);
        await server.Time.WaitForArmedAsync(ConnectBound, HangGuard.Bound);
        server.Time.Advance(ConnectBound);
        await FailEveryAttemptUntilTheMinuteAsync(server, first, cyclesBefore: 0);

        var exception = await Assert.That(first.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();
        await Assert.That(exception!.Message).IsEqualTo("The opencode server answered the event stream request but sent no event within 2 seconds.");
        await Assert.That(server.RequestCount).IsEqualTo(58);
        await Assert.That(body.IsDisposed).IsTrue();
    }

    /// <summary>Error headers count as an answer even when buffering their body never completes.</summary>
    [Test]
    [Arguments(HttpStatusCode.Unauthorized)]
    [Arguments(HttpStatusCode.InternalServerError)]
    public async Task A_Server_Whose_Error_Body_Stalls_Should_Report_An_Answer_At_Give_Up(HttpStatusCode status)
    {
        using var stalled = new BlockingContent();
        using var server = new ScriptedEventServer(number => number == 58
            ? new HttpResponseMessage(status) { Content = stalled }
            : ScriptedEventServer.Status(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError));
        await using var subscription = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var pending = PendingNext(subscription);
        for (var number = 1; number < 58; number++)
        {
            await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
            server.Time.Advance(Delay);
        }

        await stalled.ReadStarted.WaitAsync(HangGuard.Bound);
        await server.Time.WaitForArmedAsync(ConnectBound, HangGuard.Bound);
        server.Time.Advance(ConnectBound);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound, count: 2);
        server.Time.Advance(Delay);

        var failure = await Assert.That(pending.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();
        await Assert.That(failure!.Message).IsEqualTo("The opencode server answered the event stream request but sent no event within 2 seconds.");
        await Assert.That(stalled.IsDisposed).IsTrue();
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

    /// <summary>A dropped connection arms a fresh give-up minute and waits exactly one second to reconnect.</summary>
    [Test]
    public async Task A_Drop_Should_Arm_A_Fresh_Give_Up_Minute_Before_The_Reconnect_Delay()
    {
        using var connection = new LiveEventBody(ConnectedFrame);
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Stream(connection));
        using var caller = new CancellationTokenSource();
        var events = server.Events.SubscribeAsync(cancellationToken: caller.Token).GetAsyncEnumerator();
        await WithPendingCancellationAsync(caller, events, async startPull =>
        {
            // Drain creation notifications for the give-up, attempt and response-progress windows.
            for (var timer = 0; timer < 3; timer++)
            {
                await server.Time.WaitForTimerAsync(HangGuard.Bound);
            }

            connection.End();
            startPull();
            await server.Time.WaitForTimerAsync(HangGuard.Bound);

            await Assert.That(server.Time.ArmedIn(TimeSpan.FromMinutes(1))).IsEqualTo(1);
            await Assert.That(server.Time.ArmedIn(Delay)).IsEqualTo(1);
        });
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
        using var server = new ScriptedEventServer(_ => ScriptedEventServer.Stream(body));
        using var caller = new CancellationTokenSource();
        await using var events = server.Events.SubscribeAsync(cancellationToken: caller.Token).GetAsyncEnumerator();
        await Assert.That(await NextAsync(events)).IsTypeOf<EventServerConnected>();
        server.Time.Advance(TimeSpan.FromMinutes(10));
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

    private static RecordingHttpHandler CreateReconnectHandler() =>
        new(static _ => ScriptedEventServer.StreamOf(ConnectedFrame, ends: true));

    /// <summary>Asserts the pull ends in the caller's own cancellation with the stream's canceled text.</summary>
    private static async Task AssertReadCanceledByCallerAsync(Task<OperationCanceledException?> pending, CancellationToken caller)
    {
        // The caller's token is already canceled: it is the identity under test, so the wait must not observe it.
        var failure = await Assert.That(pending.WaitAsync(HangGuard.Bound, CancellationToken.None)).ThrowsNothing();
        await Assert.That(failure).IsNotNull();
        await Assert.That(failure!.CancellationToken).IsEqualTo(caller);
        await Assert.That(failure.Message).IsEqualTo("The opencode event stream read was canceled.");
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

    /// <summary>Checks the opening failure's message while the subscription and its transport are still owned.</summary>
    private static async Task AssertOpeningTransportFailureMessageAsync(ManualTimeProvider time,
        Func<HttpRequestMessage, HttpResponseMessage> answer, string expectedMessage)
    {
        using var handler = new RecordingHttpHandler(answer);
        using var httpClient = new HttpClient(handler);
        using var pipeline = PipelineFactory.Create(httpClient, time: time);
        await using var events = new EventsClient(pipeline).SubscribeAsync().GetAsyncEnumerator();
        var failure = await Assert.That(PendingNext(events).WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();

        await Assert.That(failure!.Message).IsEqualTo(expectedMessage);
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

    /// <summary>Opens the event bus through the generated door or straight through the stream spine it forwards to.</summary>
    private static IAsyncEnumerable<IEvent> Subscribe(string entry, Pipeline pipeline, OpenCodeRequestOptions options) => entry == "door"
        ? new EventsClient(pipeline).SubscribeAsync(requestOptions: options)
        : pipeline.ExecuteStreamAsync("event.subscribe", HttpMethod.Get, "/api/event", EventSubscribeResponseStreamAdapter.Instance, options, CancellationToken.None);

    [Test]
    public async Task The_Callers_Cancellation_Should_Win_Over_Disposal_During_The_Delay()
    {
        using var server = new ScriptedEventServer(static _ => throw new HttpRequestException());
        using var caller = new CancellationTokenSource();
        await using var events = server.Events.SubscribeAsync(cancellationToken: caller.Token).GetAsyncEnumerator();

        // The failing send keeps link registration and this registration on the same thread.
        // Do not insert an incomplete await between starting the pull and registering disposal.
        var pending = PendingCancellationAsync(events);
        using var disposal = caller.Token.Register(server.DisposeClient);
        await server.Time.WaitForArmedAsync(Delay, HangGuard.Bound);
        await caller.CancelOnWorkerAsync();

        await AssertReadCanceledByCallerAsync(pending, caller.Token);
    }

    [Test]
    public async Task A_First_Event_Refusal_Should_Report_The_Exact_First_Frame_Text()
    {
        using var server = new ScriptedEventServer(static _ =>
            ScriptedEventServer.StreamOf(WireBodyData.Frames(WireBodyData.SessionCreatedEvent)));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var first = PendingNext(events);

        await FailEveryAttemptUntilTheMinuteAsync(server, first, cyclesBefore: 59);

        var failure = await Assert.That(first.WaitAsync(HangGuard.Bound)).Throws<OpenCodeTransportException>();
        await Assert.That(failure!.Message).IsEqualTo("The opencode event stream did not start with server.connected.");
    }
}
