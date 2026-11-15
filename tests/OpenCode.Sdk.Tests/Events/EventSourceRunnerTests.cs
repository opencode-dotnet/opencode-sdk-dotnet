using System.Net;
using System.Net.Http.Headers;
using System.Text.Json;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Events.Support;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Events;

public sealed class EventSourceRunnerTests
{
    [Test]
    public async Task RecordDrop_Should_Keep_The_Queued_Prefix_Across_A_Retried_Attempt()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("before-drop");
        await scenario.PublishPrefixAsync("queued-one", "queued-two");
        var failure = new OpenCodeTransportException("retryable read");
        var state = await scenario.FailAttemptAsync(scenario.CurrentAttempt!, failure);
        await Assert.That(state.Status).IsEqualTo(EventConnectionStatus.Reconnecting);
        await Assert.That(events.RetainedCount).IsEqualTo(2);
        scenario.Time.Advance(EventStreamSteps.Delay);
        var retry = await scenario.Attempts.NextOpenAsync();
        await retry.PublishAsync(SharedEventFeedScenario.Marker("after-drop"));

        string?[] delivered = [(await EventStreamSteps.NextAsync(events)).Id, (await EventStreamSteps.NextAsync(events)).Id,
            (await EventStreamSteps.NextAsync(events)).Id];
        await Assert.That(delivered).IsEquivalentTo((string?[])["queued-one", "queued-two", "after-drop"], CollectionOrdering.Matching);
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(2);
    }

    [Test]
    public async Task OpenAsync_Should_Fault_Synchronously_Without_Retry_And_Allow_A_Fresh_Subscription()
    {
        await using var scenario = new SharedEventFeedScenario();
        var failure = new InvalidOperationException("synchronous source creation");
        var opened = new List<EventSource>();
        scenario.Attempts.OnOpen = () =>
        {
            opened.Add(scenario.Feed.GetCurrentSource()!);
            throw failure;
        };
        for (var attempt = 1; attempt <= 2; attempt++)
        {
            await using var events = scenario.CreateSubscription();
            await using var watcher = scenario.CreateWatcher();
            Task<bool>? moving = null;
            Task<bool>? connecting = null;
            scenario.Feed.WithGate(_ =>
            {
                moving = EventStreamSteps.PendingNext(events);
                connecting = EventStreamSteps.PendingNext(watcher);
            });
            await Assert.That(await connecting!.WaitAsync(HangGuard.Bound)).IsTrue();
            await Assert.That(watcher.Current.Status).IsEqualTo(EventConnectionStatus.Connecting);
            var idle = await EventStreamSteps.NextAsync(watcher);
            await Assert.That(idle.Status).IsEqualTo(EventConnectionStatus.Idle);
            var observed = await Assert.That(moving!).Throws<InvalidOperationException>();
            await Assert.That(observed).IsSameReferenceAs(failure);
            await scenario.Feed.JoinAsync().WaitAsync(HangGuard.Bound);
            await Assert.That(opened.Count).IsEqualTo(attempt);
            await Assert.That(opened[^1].Token.IsCancellationRequested).IsTrue();
            await Assert.That(scenario.Time.ArmedIn(EventStreamSteps.Delay)).IsEqualTo(0);
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SubscribeAsync_Should_Not_Reconnect_The_Durable_Session_Log(bool failureFrame)
    {
        var fixtures = new FixtureLoader();
        var tail = failureFrame
            ? WireBodyData.NamedFrame("effect/http-api/stream/failure", WireBodyData.StreamFailureCause)
            : WireBodyData.Frames(WireBodyData.LogSyncedEvent);
        using var scenario = ContractScenario.RespondingWithFrames(WireBodyData.Frames(
            fixtures.LoadJson("Events.session-created.json"), fixtures.LoadJson("Events.session-deleted.json")) + tail);
        var received = new List<ISessionLogItem>();
        async Task ReadAsync()
        {
            await foreach (var item in scenario.Client.Experimental.ReadSessionLogAsync("ses_9"))
            {
                received.Add(item);
            }
        }

        var reading = ReadAsync();
        if (failureFrame)
        {
            var failure = await Assert.That(reading.WaitAsync(HangGuard.Bound)).Throws<OpenCodeStreamFailureException>();
            await Assert.That(((StreamFailureCauseDie)failure!.Causes.Single()).Defect.GetString()).IsEqualTo("boom");
        }
        else
        {
            _ = await Assert.That(reading.WaitAsync(HangGuard.Bound)).ThrowsNothing();
        }

        string[] expected = failureFrame ? ["session.created", "session.deleted"] : ["session.created", "session.deleted", "log.synced"];
        await Assert.That(received.Select(static item => item.Type)).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(((SessionCreated)received[0]).Id).IsEqualTo("evt_1");
        await Assert.That(((SessionDeleted)received[1]).Id).IsEqualTo("evt_2");
        await Assert.That(scenario.Requests.Single().RequestUri!.AbsolutePath).IsEqualTo("/api/experimental/session/ses_9/log");
        var state = scenario.Client.Events.GetConnectionState();
        await Assert.That(state.Status).IsEqualTo(EventConnectionStatus.Idle);
        await Assert.That(state.Attempt).IsEqualTo(0);
        await Assert.That(state.Error).IsNull();
    }

    [Test]
    public async Task GetConnectionState_Should_Start_A_Fresh_Retry_Counter_After_A_Source_Fault()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = scenario.Feed.Subscribe(EventSelection.Capture(null), default).GetAsyncEnumerator();
        var moving = EventStreamSteps.PendingNext(events);
        var failed = await scenario.Attempts.NextOpenAsync();
        _ = failed.Items.Writer.TryComplete(new OpenCodeTransportException("first failure"));
        failed.ReleaseCleanup();
        await scenario.Time.WaitForArmedAsync(EventStreamSteps.Delay, HangGuard.Bound);
        scenario.Time.Advance(EventStreamSteps.Delay);
        var connected = await scenario.Attempts.NextOpenAsync();
        await connected.PublishAsync(SharedEventFeedScenario.Marker("M"));
        await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsTrue();
        connected.CleanupFailure = new InvalidOperationException("escaping cleanup");
        _ = connected.Items.Writer.TryComplete();
        connected.ReleaseCleanup();
        await scenario.Feed.JoinAsync().WaitAsync(HangGuard.Bound);
        await using var fresh = await scenario.AdmitAsync("M2");
        var failure = new OpenCodeTransportException("fresh failure");
        var state = await scenario.FailAttemptAsync(scenario.CurrentAttempt!, failure);
        await Assert.That(state.Status).IsEqualTo(EventConnectionStatus.Reconnecting);
        await Assert.That(state.Attempt).IsEqualTo(1);
        await Assert.That(state.Error).IsSameReferenceAs(failure);
        await Assert.That(fresh.Current.Id).IsEqualTo("M2");
    }

    [Test]
    [Arguments(400, WireBodyData.InvalidRequestError, typeof(InvalidRequestError))]
    [Arguments(401, WireBodyData.UnauthorizedError, typeof(UnauthorizedError))]
    public async Task SubscribeAsync_Should_Keep_Retrying_Past_One_Minute_And_Expose_The_Last_Attempt_Error(
        int status, string body, Type errorType)
    {
        const int failures = 61;
        using var server = new ScriptedEventServer(index => index <= failures
            ? ScriptedEventServer.Status((HttpStatusCode)status, body)
            : ScriptedEventServer.StreamOf(EventStreamSteps.ConnectedFrame));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var moving = EventStreamSteps.PendingNext(events);
        using var waiter = new CancellationTokenSource();
        Task? armed = null;
        try
        {
            for (var attempt = 1; attempt <= failures; attempt++)
            {
                armed = server.Time.WaitForArmedOrCanceledAsync(EventStreamSteps.Delay, HangGuard.Bound, waiter.Token);
                var completed = await Task.WhenAny(armed, moving).WaitAsync(HangGuard.Bound);
                await Assert.That(completed).IsSameReferenceAs(armed);
                await armed;
                var state = server.Events.GetConnectionState();
                await Assert.That(state.Status).IsEqualTo(EventConnectionStatus.Reconnecting);
                await Assert.That(state.Attempt).IsEqualTo(attempt);
                await Assert.That(state.Error).IsTypeOf<OpenCodeApiException>();
                var failure = (OpenCodeApiException)state.Error!;
                await Assert.That(failure.Status).IsEqualTo(status);
                await Assert.That(failure.Error!.GetType()).IsEqualTo(errorType);
                await Assert.That(failure.RawBody).IsEqualTo(body);
                server.Time.Advance(EventStreamSteps.Delay);
            }
        }
        finally
        {
            await waiter.CancelOnWorkerAsync();
            if (armed is { IsCanceled: true })
            {
                _ = await Assert.That(armed).Throws<OperationCanceledException>();
            }
        }

        await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current.Type).IsEqualTo("server.connected");
        await Assert.That(server.RequestCount).IsEqualTo(62);
        var connected = server.Events.GetConnectionState();
        await Assert.That(connected.Status).IsEqualTo(EventConnectionStatus.Connected);
        await Assert.That(connected.Attempt).IsEqualTo(0);
        await Assert.That(connected.Error).IsNull();
    }

    [Test]
    public async Task GetConnectionState_Should_Report_The_Latest_Classified_Failure()
    {
        using var server = new ScriptedEventServer(index => index switch
        {
            1 => ScriptedEventServer.Status(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError),
            2 => ScriptedEventServer.Status(HttpStatusCode.InternalServerError, WireBodyData.UnauthorizedError),
            _ => ScriptedEventServer.StreamOf(EventStreamSteps.ConnectedFrame),
        });
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var moving = EventStreamSteps.PendingNext(events);
        await server.Time.WaitForArmedAsync(EventStreamSteps.Delay, HangGuard.Bound);
        var first = server.Events.GetConnectionState().Error;
        await Assert.That(((OpenCodeApiException)first!).Status).IsEqualTo(401);
        server.Time.Advance(EventStreamSteps.Delay);
        await server.Time.WaitForArmedAsync(EventStreamSteps.Delay, HangGuard.Bound);
        var latest = server.Events.GetConnectionState();
        await Assert.That(latest.Attempt).IsEqualTo(2);
        await Assert.That(latest.Error).IsNotSameReferenceAs(first);
        await Assert.That(((OpenCodeApiException)latest.Error!).Status).IsEqualTo(500);
        server.Time.Advance(EventStreamSteps.Delay);
        await EventStreamSteps.AssertConnectedAsync(events, await moving.WaitAsync(HangGuard.Bound));
    }

    [Test]
    public async Task GetConnectionState_Should_Report_The_Ended_Text_Without_Ending_The_Subscription()
    {
        using var server = new ScriptedEventServer(index => ScriptedEventServer.StreamOf(EventStreamSteps.ConnectedFrame, ends: index == 1));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        await Assert.That((await EventStreamSteps.NextAsync(events)).Type).IsEqualTo("server.connected");
        var moving = EventStreamSteps.PendingNext(events);
        await server.Time.WaitForArmedAsync(EventStreamSteps.Delay, HangGuard.Bound);
        var state = server.Events.GetConnectionState();
        await Assert.That(state.Error).IsTypeOf<OpenCodeTransportException>();
        await Assert.That(state.Error!.Message).IsEqualTo("The opencode event stream ended.");
        server.Time.Advance(EventStreamSteps.Delay);
        await EventStreamSteps.AssertConnectedAsync(events, await moving.WaitAsync(HangGuard.Bound));
    }

    [Test]
    public async Task GetConnectionState_Should_Report_A_Typed_Stream_Failure_Cause_Then_Recover()
    {
        using var server = new ScriptedEventServer(index => index == 1
            ? ScriptedEventServer.StreamOf(WireBodyData.NamedFrame("effect/http-api/stream/failure", WireBodyData.StreamFailureCause))
            : ScriptedEventServer.StreamOf(EventStreamSteps.ConnectedFrame));
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var moving = EventStreamSteps.PendingNext(events);
        await server.Time.WaitForArmedAsync(EventStreamSteps.Delay, HangGuard.Bound);
        var state = server.Events.GetConnectionState();
        await Assert.That(state.Error).IsTypeOf<OpenCodeStreamFailureException>();
        var cause = (StreamFailureCauseDie)((OpenCodeStreamFailureException)state.Error!).Causes.Single();
        await Assert.That(cause.Defect.ValueKind).IsEqualTo(JsonValueKind.String);
        await Assert.That(cause.Defect.GetString()).IsEqualTo("boom");
        server.Time.Advance(EventStreamSteps.Delay);
        await EventStreamSteps.AssertConnectedAsync(events, await moving.WaitAsync(HangGuard.Bound));
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    [Arguments(3)]
    public async Task GetConnectionState_Should_Classify_Opening_And_Stalled_Error_Body_Windows_Then_Recover(int kind)
    {
        using var body = new BlockingContent();
        if (kind == 1)
        {
            body.Headers.ContentType = new MediaTypeHeaderValue("text/event-stream");
        }

        var status = kind switch
        {
            1 => HttpStatusCode.OK,
            2 => HttpStatusCode.Unauthorized,
            _ => HttpStatusCode.InternalServerError,
        };
        using var server = new ScriptedEventServer(index =>
        {
            if (index != 1)
            {
                return ScriptedEventServer.StreamOf(EventStreamSteps.ConnectedFrame);
            }

            return kind == 0 ? ScriptedEventServer.Silence : new HttpResponseMessage(status) { Content = body };
        });
        await using var events = server.Events.SubscribeAsync().GetAsyncEnumerator();
        var moving = EventStreamSteps.PendingNext(events);
        await server.WaitForRequestAsync(1, HangGuard.Bound);
        if (kind != 0)
        {
            var reading = body.ReadStarted.ContinueWith(static task => task.Status,
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            await Assert.That(await reading.WaitAsync(HangGuard.Bound)).IsEqualTo(TaskStatus.RanToCompletion);
        }

        await server.Time.WaitForArmedAsync(EventStreamSteps.ConnectBound, HangGuard.Bound);
        server.Time.Advance(EventStreamSteps.ConnectBound);
        await server.Time.WaitForArmedAsync(EventStreamSteps.Delay, HangGuard.Bound);
        var state = server.Events.GetConnectionState();
        await Assert.That(state.Status).IsEqualTo(EventConnectionStatus.Reconnecting);
        await Assert.That(state.Error).IsTypeOf<OpenCodeTransportException>();
        await Assert.That(state.Error!.Message).IsEqualTo(kind == 0
            ? "The opencode server did not answer the event stream request within 2 seconds."
            : "The opencode server answered the event stream request but sent no event within 2 seconds.");
        if (kind != 0)
        {
            await Assert.That(body.IsDisposed).IsTrue();
        }

        server.Time.Advance(EventStreamSteps.Delay);
        await EventStreamSteps.AssertConnectedAsync(events, await moving.WaitAsync(HangGuard.Bound));
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task GetConnectionState_Should_Publish_Attempt_1_After_A_Connection_That_Lived_One_Second(bool longConnection)
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = scenario.Feed.Subscribe(EventSelection.Capture(null), default).GetAsyncEnumerator();
        var moving = EventStreamSteps.PendingNext(events);
        var first = await scenario.Attempts.NextOpenAsync();
        _ = first.Items.Writer.TryComplete(new OpenCodeTransportException("first open"));
        first.ReleaseCleanup();
        await scenario.Time.WaitForArmedAsync(EventStreamSteps.Delay, HangGuard.Bound);
        scenario.Time.Advance(EventStreamSteps.Delay);
        var second = await scenario.Attempts.NextOpenAsync();
        await second.PublishAsync(SharedEventFeedScenario.Marker("M"));
        await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(scenario.Feed.GetConnectionState().Attempt).IsEqualTo(0);
        scenario.Time.Advance(longConnection ? TimeSpan.FromSeconds(1) : TimeSpan.FromSeconds(1) - EventStreamSteps.OneTick);
        var failure = new OpenCodeTransportException("connected drop");
        var state = await scenario.FailAttemptAsync(second, failure);
        await Assert.That(state.Status).IsEqualTo(EventConnectionStatus.Reconnecting);
        await Assert.That(state.Attempt).IsEqualTo(longConnection ? 1 : 2);
        await Assert.That(state.Error).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task GetConnectionState_Should_Keep_Error_Through_The_Next_Attempt_And_Clear_It_On_Connected_Or_Idle()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M");
        var failure = new OpenCodeTransportException("drop");
        _ = await scenario.FailAttemptAsync(scenario.CurrentAttempt!, failure);
        scenario.Time.Advance(EventStreamSteps.Delay);
        var replacement = await scenario.Attempts.NextOpenAsync();
        var opening = scenario.Feed.GetConnectionState();
        await Assert.That(opening.Status).IsEqualTo(EventConnectionStatus.Reconnecting);
        await Assert.That(opening.Attempt).IsEqualTo(1);
        await Assert.That(opening.Error).IsSameReferenceAs(failure);
        await replacement.PublishAsync(SharedEventFeedScenario.Marker("M2"));
        var connected = scenario.Feed.GetConnectionState();
        await Assert.That(connected.Status).IsEqualTo(EventConnectionStatus.Connected);
        await Assert.That(connected.Attempt).IsEqualTo(0);
        await Assert.That(connected.Error).IsNull();
        await events.DisposeAsync();
        var idle = scenario.Feed.GetConnectionState();
        await Assert.That(idle.Status).IsEqualTo(EventConnectionStatus.Idle);
        await Assert.That(idle.Attempt).IsEqualTo(0);
        await Assert.That(idle.Error).IsNull();
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RunAsync_Should_Not_Carry_The_First_Subscribers_ExecutionContext(bool alreadySuppressed)
    {
        var ambient = new AsyncLocal<string?> { Value = "caller scope" };
        string? observed = null;
        var observations = 0;
        await using var scenario = new SharedEventFeedScenario();
        scenario.Attempts.OnOpen = () =>
        {
            observations++;
            observed = ambient.Value;
        };
        await using var events = scenario.Feed.Subscribe(EventSelection.Capture(null), default).GetAsyncEnumerator();
        Task<bool> moving;
        if (alreadySuppressed)
        {
            using (ExecutionContext.SuppressFlow())
            {
                moving = EventStreamSteps.PendingNext(events);
            }
        }
        else
        {
            moving = EventStreamSteps.PendingNext(events);
        }

        var attempt = await scenario.Attempts.NextOpenAsync();
        await Assert.That(observations).IsEqualTo(1);
        await Assert.That(observed).IsNull();
        await Assert.That(ambient.Value).IsEqualTo("caller scope");
        await attempt.PublishAsync(SharedEventFeedScenario.Marker("M"));
        await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current.Id).IsEqualTo("M");
    }

    [Test]
    public async Task GetConnectionState_Should_Publish_Connecting_Then_Connected_Then_Reconnecting()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = scenario.Feed.Subscribe(EventSelection.Capture(null), default).GetAsyncEnumerator();
        var moving = EventStreamSteps.PendingNext(events);
        var opening = scenario.Feed.GetConnectionState();
        await Assert.That(opening.Status).IsEqualTo(EventConnectionStatus.Connecting);
        await Assert.That(opening.Attempt).IsEqualTo(0);
        await Assert.That(opening.Error).IsNull();
        var attempt = await scenario.Attempts.NextOpenAsync();
        await attempt.PublishAsync(SharedEventFeedScenario.Marker("M"));
        await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(events.Current.Id).IsEqualTo("M");
        var connected = scenario.Feed.GetConnectionState();
        await Assert.That(connected.Status).IsEqualTo(EventConnectionStatus.Connected);
        await Assert.That(connected.Attempt).IsEqualTo(0);
        await Assert.That(connected.Error).IsNull();
        var failure = new OpenCodeTransportException("lost connection");
        var reconnecting = await scenario.FailAttemptAsync(attempt, failure);
        await Assert.That(reconnecting.Status).IsEqualTo(EventConnectionStatus.Reconnecting);
        await Assert.That(reconnecting.Attempt).IsEqualTo(1);
        await Assert.That(reconnecting.Error).IsSameReferenceAs(failure);
    }
}
