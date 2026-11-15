using System.Reflection;
using System.Text.Json;
using System.Text.Json.Nodes;
using System.Threading.Channels;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Events.Support;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Events;

public sealed class SharedEventFeedTests
{
    [Test]
    public async Task SubscribeAsync_Should_Create_Queue_And_Watch_State_Only_For_Enumerators()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var recipe = (EventSubscription<IEvent>)scenario.Feed.Subscribe(EventSelection.Capture(null), default);
        await using var watchRecipe = (EventStateWatcher)scenario.Feed.Watch(default);
        var queue = typeof(EventSubscription<IEvent>).GetField("_queue", BindingFlags.NonPublic | BindingFlags.Instance)!;
        var watch = typeof(EventStateWatcher).GetField("_subscription", BindingFlags.NonPublic | BindingFlags.Instance)!;
        await Assert.That(queue.GetValue(recipe)).IsNull();
        await Assert.That(watch.GetValue(watchRecipe)).IsNull();
        await using var events = recipe.GetAsyncEnumerator();
        await using var watcher = watchRecipe.GetAsyncEnumerator();

        await Assert.That(queue.GetValue(events)).IsNotNull();
        await Assert.That(watch.GetValue(watcher)).IsNotNull();
        await Assert.That(scenario.Feed.GetCurrentSource()).IsNull();
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(0);
    }

    [Test]
    public async Task SubscribeAsync_Should_Replay_The_Latest_Marker_Without_Business_History()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var first = await scenario.AdmitAsync("marker-one");
        var lateRecipe = scenario.Feed.Subscribe(EventSelection.Capture(null), default);
        var latest = SharedEventFeedScenario.Marker("marker-two");
        await scenario.CurrentAttempt!.PublishAsync(latest);
        await scenario.PublishAsync("earlier-business");
        await using var late = lateRecipe.GetAsyncEnumerator();

        await Assert.That(await EventStreamSteps.NextAsync(late)).IsSameReferenceAs(latest);
        await scenario.PublishAsync("later-business");
        await Assert.That((await EventStreamSteps.NextAsync(late)).Id).IsEqualTo("later-business");
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(1);
    }

    [Test]
    public async Task Complete_Should_Propagate_The_Current_Runners_Faulted_Task()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("faulted-task-marker");
        var source = scenario.Feed.GetCurrentSource()!;
        var failure = new InvalidOperationException("faulted task");
        scenario.Feed.Complete(source, Task.FromException(failure));

        await Assert.That(scenario.Feed.GetCurrentSource()).IsNull();
        var observed = await Assert.That(async () => await events.MoveNextAsync()).Throws<InvalidOperationException>();
        await Assert.That(observed).IsSameReferenceAs(failure);
    }

    private readonly FixtureLoader _fixtures = new();

    [Test]
    public async Task SubscribeAsync_Should_Not_Count_Excluded_Events_Toward_Capacity()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var owner = await scenario.AdmitAsync("M");
        await using var selected = (EventSubscription<IEvent>)scenario.Feed.Subscribe(
            EventSelection.Capture([new("rpc.sdk-test")]), default).GetAsyncEnumerator();
        await Assert.That((await EventStreamSteps.NextAsync(selected)).Type).IsEqualTo("server.connected");
        var excluded = new LocationShutdown { Id = "excluded", Created = 0, Data = new Dictionary<string, JsonElement>(StringComparer.Ordinal) };
        for (var index = 0; index < 4097; index++)
        {
            await scenario.CurrentAttempt!.PublishAsync(excluded);
        }

        await Assert.That(selected.RetainedCount).IsEqualTo(0);
        await scenario.PublishAsync("selected");
        await Assert.That((await EventStreamSteps.NextAsync(selected)).Id).IsEqualTo("selected");
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(1);
    }

    [Test]
    public async Task SubscribeAsync_Should_Give_Two_Enumerations_Of_One_Result_Two_Subscriptions()
    {
        await using var scenario = new SharedEventFeedScenario();
        using var caller = new CancellationTokenSource();
        var recipe = scenario.Feed.Subscribe(EventSelection.Capture(null), default);
        await using var first = recipe.GetAsyncEnumerator(caller.Token);
        await using var second = recipe.GetAsyncEnumerator();
        var firstMove = EventStreamSteps.PendingNext(first);
        var secondMove = EventStreamSteps.PendingNext(second);
        var attempt = await scenario.Attempts.NextOpenAsync();
        await attempt.PublishAsync(SharedEventFeedScenario.Marker("M"));
        await Assert.That(await firstMove.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(await secondMove.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(second.Current).IsSameReferenceAs(first.Current);
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(1);
        await caller.CancelOnWorkerAsync();
        await attempt.PublishAsync(SharedEventFeedScenario.Event("E1"));
        await Assert.That((await EventStreamSteps.NextAsync(second)).Id).IsEqualTo("E1");
        var canceled = await EventStreamSteps.PendingCancellationAsync(first).WaitAsync(HangGuard.Bound);
        await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
        await using var otherRoot = new SharedEventFeedScenario();
        await using var other = await otherRoot.AdmitAsync("other-root");
        await Assert.That(other.Current.Id).IsEqualTo("other-root");
        await Assert.That(otherRoot.Attempts.OpenCount).IsEqualTo(1);
        await Assert.That(otherRoot.Feed.GetCurrentSource()).IsNotSameReferenceAs(scenario.Feed.GetCurrentSource());
    }

    [Test]
    public async Task MoveNextAsync_Should_Admit_Lazily_And_Open_Nothing_After_Pre_Cancellation_Or_Pre_Disposal()
    {
        await using var scenario = new SharedEventFeedScenario();
        var recipe = scenario.Feed.Subscribe(EventSelection.Capture(null), default);
        await using var disposed = recipe.GetAsyncEnumerator();
        await Assert.That(scenario.Feed.GetCurrentSource()).IsNull();
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(0);
        await disposed.DisposeAsync();
        await Assert.That(await disposed.MoveNextAsync()).IsFalse();
        using var caller = new CancellationTokenSource();
        await caller.CancelOnWorkerAsync();
        await using var canceled = recipe.GetAsyncEnumerator(caller.Token);
        var failure = await EventStreamSteps.PendingCancellationAsync(canceled).WaitAsync(HangGuard.Bound);
        await Assert.That(failure!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(0);
        await using var live = await scenario.AdmitAsync("M");
        await Assert.That(live.Current.Id).IsEqualTo("M");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SubscribeAsync_Should_Not_Replay_Business_Events_When_Publication_Wins(bool holdReadBeforeAdmission)
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var owner = await scenario.AdmitAsync("M");
        var marker = owner.Current;
        await scenario.PublishAsync("E1");
        var release = Channel.CreateBounded<bool>(1);
        var read = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        Task? publishing = null;
        if (holdReadBeforeAdmission)
        {
            scenario.CurrentAttempt!.BeforeReturn = async item =>
            {
                if (string.Equals(item.Id, "E2", StringComparison.Ordinal))
                {
                    read.SetResult();
                    _ = await release.Reader.ReadAsync(CancellationToken.None);
                }
            };
            publishing = scenario.PublishAsync("E2");
            await read.Task.WaitAsync(HangGuard.Bound);
        }

        try
        {
            await using var late = await scenario.AdmitAsync("M");
            await Assert.That(late.Current).IsSameReferenceAs(marker);
            if (publishing is null)
            {
                publishing = scenario.PublishAsync("E2");
            }

            _ = release.Writer.TryWrite(true);
            await publishing.WaitAsync(HangGuard.Bound);
            string?[] received = [marker.Id, (await EventStreamSteps.NextAsync(late)).Id];
            await Assert.That(received).IsEquivalentTo((string?[])["M", "E2"], CollectionOrdering.Matching);
        }
        finally
        {
            _ = release.Writer.TryWrite(true);
            if (publishing is not null)
            {
                await publishing.WaitAsync(HangGuard.Bound);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task SubscribeAsync_Should_Wait_For_A_New_Marker_When_A_Drop_Precedes_Admission(bool admissionFirst)
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var owner = await scenario.AdmitAsync("M1");
        var source = scenario.Feed.GetCurrentSource()!;
        await using var late = scenario.Feed.Subscribe(EventSelection.Capture(null), default).GetAsyncEnumerator();
        Task<bool> moving;
        if (admissionFirst)
        {
            moving = EventStreamSteps.PendingNext(late);
            await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsTrue();
            await Assert.That(late.Current.Id).IsEqualTo("M1");
            scenario.Feed.RecordDrop(source, new OpenCodeTransportException("drop"), 1);
            moving = EventStreamSteps.PendingNext(late);
        }
        else
        {
            scenario.Feed.RecordDrop(source, new OpenCodeTransportException("drop"), 1);
            moving = EventStreamSteps.PendingNext(late);
        }

        scenario.Feed.RecordConnected(source, SharedEventFeedScenario.Marker("M2"));
        scenario.Feed.Publish(source, SharedEventFeedScenario.Event("E2"));
        await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsTrue();
        string?[] received = [late.Current.Id, (await EventStreamSteps.NextAsync(late)).Id];
        await Assert.That(received).IsEquivalentTo((string?[])["M2", "E2"], CollectionOrdering.Matching);
    }

    [Test]
    [Arguments(0)]
    [Arguments(1)]
    [Arguments(2)]
    public async Task SubscribeAsync_Should_Select_Exact_Types_And_Always_Include_The_Marker(int kind)
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var owner = await scenario.AdmitAsync("M");
        IReadOnlyList<EventType>? types = kind switch
        {
            0 => [EventType.SessionCreated, new("rpc.sdk-test")],
            1 => null,
            _ => [],
        };
        await using var selected = scenario.Feed.Subscribe(EventSelection.Capture(types), default).GetAsyncEnumerator();
        var marker = await EventStreamSteps.NextAsync(selected);
        var excluded = new LocationShutdown { Id = "excluded", Created = 0, Data = new Dictionary<string, JsonElement>(StringComparer.Ordinal) };
        var native = JsonSerializer.Deserialize(_fixtures.LoadJson("Events.session-created.json"), OpenCodeJsonContext.Default.IEvent)!;
        var unknown = JsonSerializer.Deserialize(_fixtures.LoadJson("Events.unknown-log-event.json"), OpenCodeJsonContext.Default.IEvent)!;
        var drifted = JsonNode.Parse(ScriptedEventServer.Connected)!.AsObject();
        drifted["data"] = false;
        using var document = JsonDocument.Parse(drifted.ToJsonString());
        IEvent driftedMarker = new UnknownEvent("server.connected", document.RootElement);
        var candidates = new IEvent[]
        {
            excluded, native, SharedEventFeedScenario.Event("other") with { Type = "rpc.other" },
            SharedEventFeedScenario.Event("requested"), unknown, driftedMarker,
        };
        foreach (var item in candidates)
        {
            await scenario.CurrentAttempt!.PublishAsync(item);
        }

        string[] expected = kind == 0
            ? ["server.connected", "session.created", "rpc.sdk-test", "server.connected"]
            : ["server.connected", "location.shutdown", "session.created", "rpc.other", "rpc.sdk-test", "session.invented.tomorrow", "server.connected"];
        var actual = new List<string> { marker.Type };
        for (var index = 1; index < expected.Length; index++)
        {
            actual.Add((await EventStreamSteps.NextAsync(selected)).Type);
        }

        await Assert.That(actual).IsEquivalentTo(expected, CollectionOrdering.Matching);
        await Assert.That(selected.Current).IsSameReferenceAs(driftedMarker);
        await Assert.That(driftedMarker).IsTypeOf<UnknownEvent>();
    }

    [Test]
    public async Task SubscribeAsync_Should_Share_Event_And_Metadata_Instances_Between_Subscriptions()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var first = await scenario.AdmitAsync("M");
        await using var second = await scenario.AdmitAsync("M");
        var metadata = new Dictionary<string, JsonElement>(StringComparer.Ordinal);
        var native = (SessionCreated)JsonSerializer.Deserialize(_fixtures.LoadJson("Events.session-created.json"), OpenCodeJsonContext.Default.IEvent)!;
        await scenario.CurrentAttempt!.PublishAsync(native with { Metadata = metadata });
        var right = await EventStreamSteps.SharedEventAsync(first, second, "evt_1");
        var left = first.Current;
        await Assert.That(right.Metadata).IsSameReferenceAs(metadata);
        metadata.Add("controlled-mutation", default);
        await Assert.That(left.Metadata).ContainsKey("controlled-mutation");
        await Assert.That(right.Metadata!["controlled-mutation"].ValueKind).IsEqualTo(JsonValueKind.Undefined);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RecordConnected_Should_Ignore_A_Retired_Source_After_Its_Replacement_Connects(bool cleanupFirst)
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var retired = await scenario.AdmitAsync("M1");
        var oldSource = scenario.Feed.GetCurrentSource()!;
        var oldAttempt = scenario.CurrentAttempt!;
        oldAttempt.CleanupFailure = new InvalidOperationException("retired cleanup");
        await retired.DisposeAsync();
        if (cleanupFirst)
        {
            oldAttempt.ReleaseCleanup();
            await scenario.Feed.JoinAsync().WaitAsync(HangGuard.Bound);
        }

        await using var current = await scenario.AdmitAsync("M2");
        var marker = current.Current;
        var source = scenario.Feed.GetCurrentSource();
        var state = scenario.Feed.GetConnectionState();
        scenario.Feed.RecordConnected(oldSource, SharedEventFeedScenario.Marker("stale"));
        scenario.Feed.Publish(oldSource, SharedEventFeedScenario.Event("stale-event"));
        scenario.Feed.RecordDrop(oldSource, new OpenCodeTransportException("stale drop"), 1);
        await Assert.That(scenario.Feed.RecordAttemptStarted(oldSource)).IsFalse();
        scenario.Feed.RecordFault(oldSource, new InvalidOperationException("stale fault"));
        await Assert.That(scenario.Feed.GetCurrentSource()).IsSameReferenceAs(source);
        await Assert.That(scenario.Feed.GetConnectionState()).IsSameReferenceAs(state);
        await Assert.That(current.RetainedCount).IsEqualTo(0);
        oldAttempt.ReleaseCleanup();
        var observed = oldSource.RunnerObservation!.ContinueWith(static task => task.Status,
            CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
        await Assert.That(await observed.WaitAsync(HangGuard.Bound)).IsEqualTo(TaskStatus.RanToCompletion);
        await using var late = await scenario.AdmitAsync("M2");

        await Assert.That(late.Current).IsSameReferenceAs(marker);
        await scenario.PublishAsync("E2");
        string?[] received = [(await EventStreamSteps.NextAsync(current)).Id, (await EventStreamSteps.NextAsync(late)).Id];
        await Assert.That(received).IsEquivalentTo((string?[])["E2", "E2"], CollectionOrdering.Matching);
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(2);
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Dispose_Should_Fence_Publication_And_Close_Admission_Before_Cancelling_The_Runner(bool publicationFirst)
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M");
        var source = scenario.Feed.GetCurrentSource()!;
        var pending = EventStreamSteps.PendingNext(events);
        if (publicationFirst)
        {
            scenario.Feed.Publish(source, SharedEventFeedScenario.Event("E1"));
        }

        scenario.Feed.Dispose();
        scenario.Feed.Publish(source, SharedEventFeedScenario.Event("rejected"));
        scenario.Feed.RecordConnected(source, SharedEventFeedScenario.Marker("rejected-marker"));
        if (publicationFirst)
        {
            await Assert.That(await pending.WaitAsync(HangGuard.Bound)).IsTrue();
            await Assert.That(events.Current.Id).IsEqualTo("E1");
        }
        else
        {
            _ = await Assert.That(pending).Throws<ObjectDisposedException>();
        }

        _ = await Assert.That(async () => await events.MoveNextAsync()).Throws<ObjectDisposedException>();
        await Assert.That(events.RetainedCount).IsEqualTo(0);
        await using var late = scenario.Feed.Subscribe(EventSelection.Capture(null), default).GetAsyncEnumerator();
        _ = await Assert.That(async () => await late.MoveNextAsync()).Throws<ObjectDisposedException>();
        await Assert.That(scenario.Feed.GetCurrentSource()).IsNull();
    }

    [Test]
    [Arguments("cancel")]
    [Arguments("dispose")]
    [Arguments("overflow")]
    public async Task EndLocked_Should_Stop_The_Source_Only_When_The_Last_Subscription_Leaves(string ending)
    {
        using var caller = new CancellationTokenSource();
        await using var scenario = new SharedEventFeedScenario();
        await using var first = await scenario.AdmitAsync("M", caller.Token);
        await using var second = await scenario.AdmitAsync("M");
        var source = scenario.Feed.GetCurrentSource()!;
        if (string.Equals(ending, "cancel", StringComparison.Ordinal))
        {
            await caller.CancelOnWorkerAsync();
        }
        else if (string.Equals(ending, "dispose", StringComparison.Ordinal))
        {
            await first.DisposeAsync();
        }
        else
        {
            for (var index = 0; index < 4097; index++)
            {
                await scenario.PublishAsync("overflow");
                await Assert.That((await EventStreamSteps.NextAsync(second)).Id).IsEqualTo("overflow");
            }

            _ = await Assert.That(async () => await first.MoveNextAsync()).Throws<OpenCodeEventSubscriberOverflowException>();
        }

        await scenario.PublishAsync("E2");
        await Assert.That((await EventStreamSteps.NextAsync(second)).Id).IsEqualTo("E2");
        await Assert.That(scenario.Feed.GetCurrentSource()).IsSameReferenceAs(source);
        await Assert.That(source.Token.IsCancellationRequested).IsFalse();
        await second.DisposeAsync();
        await Assert.That(scenario.Feed.GetCurrentSource()).IsNull();
        scenario.CurrentAttempt!.ReleaseCleanup();
        await scenario.Feed.JoinAsync().WaitAsync(HangGuard.Bound);
        await Assert.That(source.Token.IsCancellationRequested).IsTrue();
        await Assert.That(source.IsReleased).IsTrue();
    }

    [Test]
    public async Task Publish_Should_Not_Run_A_Consumer_Continuation_Inside_The_Gate()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M");
        var source = scenario.Feed.GetCurrentSource()!;
        using var publishingScope = new ThreadLocal<bool>(static () => false);
        var moving = EventStreamSteps.PendingNext(events);
        var consumer = moving.ContinueWith(completed =>
        {
            _ = scenario.Feed.GetCurrentSource();
            return publishingScope.Value;
        }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
        var publishing = DedicatedThread.Run(() =>
        {
            publishingScope.Value = true;
            try
            {
                scenario.Feed.Publish(source, SharedEventFeedScenario.Event("E1"));
            }
            finally
            {
                publishingScope.Value = false;
            }
        });

        await publishing.WaitAsync(HangGuard.Bound);
        await Assert.That(await consumer.WaitAsync(HangGuard.Bound)).IsFalse();
        await Assert.That(events.Current.Id).IsEqualTo("E1");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task Complete_Should_Fault_Every_Subscription_When_The_Current_Runner_Returns_Or_Is_Cancelled(bool canceled)
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var first = await scenario.AdmitAsync("M");
        await using var second = await scenario.AdmitAsync("M");
        var source = scenario.Feed.GetCurrentSource()!;
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelOnWorkerAsync();
        scenario.Feed.Complete(source, canceled ? Task.FromCanceled(cancellation.Token) : Task.CompletedTask);
        await Assert.That(scenario.Feed.GetCurrentSource()).IsNull();

        var left = await Assert.That(async () => await first.MoveNextAsync()).Throws<Exception>();
        var right = await Assert.That(async () => await second.MoveNextAsync()).Throws<Exception>();
        await Assert.That(left).IsSameReferenceAs(right);
        if (canceled)
        {
            await Assert.That(left).IsTypeOf<TaskCanceledException>();
        }
        else
        {
            await Assert.That(left).IsTypeOf<InvalidOperationException>();
            await Assert.That(left!.Message).IsEqualTo("The current event source returned without an ending.");
        }

        await Assert.That(scenario.Feed.GetCurrentSource()).IsNull();
    }
}
