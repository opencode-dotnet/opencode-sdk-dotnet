using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Events.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Events;

public sealed class EventStateWatcherTests
{
    [Test]
    public async Task WatchConnectionAsync_Should_Remain_Alive_And_Report_Idle_After_A_Source_Fault()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M");
        await using var watcher = scenario.Feed.Watch(default).GetAsyncEnumerator();
        await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(watcher.Current.Status).IsEqualTo(EventConnectionStatus.Connected);
        var failure = new InvalidOperationException("source cleanup");
        await scenario.FaultAsync(failure);

        await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(watcher.Current.Status).IsEqualTo(EventConnectionStatus.Idle);
        await Assert.That(watcher.Current.Attempt).IsEqualTo(0);
        await Assert.That(watcher.Current.Error).IsNull();
        var observed = await Assert.That(async () => await events.MoveNextAsync()).Throws<InvalidOperationException>();
        await Assert.That(observed).IsSameReferenceAs(failure);
        await using var replacement = await scenario.AdmitAsync("M2");
        await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(watcher.Current.Status).IsEqualTo(EventConnectionStatus.Connected);
        await Assert.That(replacement.Current.Id).IsEqualTo("M2");
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task WatchConnectionAsync_Should_Select_The_Initial_Snapshot_Atomically_With_Registration(bool watchFirst)
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M");
        var source = scenario.Feed.GetCurrentSource()!;
        await using var watcher = scenario.Feed.Watch(default).GetAsyncEnumerator();
        if (watchFirst)
        {
            await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsTrue();
            await Assert.That(watcher.Current.Status).IsEqualTo(EventConnectionStatus.Connected);
        }

        var failure = new OpenCodeTransportException("drop");
        scenario.Feed.RecordDrop(source, failure, 1);
        await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(watcher.Current.Status).IsEqualTo(EventConnectionStatus.Reconnecting);
        await Assert.That(watcher.Current.Attempt).IsEqualTo(1);
        await Assert.That(watcher.Current.Error).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task WatchConnectionAsync_Should_Coalesce_To_The_Latest_Unread_Snapshot()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M");
        var source = scenario.Feed.GetCurrentSource()!;
        await using var watcher = scenario.Feed.Watch(default).GetAsyncEnumerator();
        await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsTrue();
        var earlier = new OpenCodeTransportException("earlier");
        var latest = new OpenCodeTransportException("latest");
        scenario.Feed.RecordDrop(source, earlier, 1);
        scenario.Feed.RecordDrop(source, latest, 2);
        var moving = EventStreamSteps.PendingNext(watcher);
        var after = new OpenCodeTransportException("after selection");
        scenario.Feed.RecordDrop(source, after, 3);
        await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(watcher.Current.Status).IsEqualTo(EventConnectionStatus.Reconnecting);
        await Assert.That(watcher.Current.Attempt).IsEqualTo(2);
        await Assert.That(watcher.Current.Error).IsSameReferenceAs(latest);
        await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(watcher.Current.Attempt).IsEqualTo(3);
        await Assert.That(watcher.Current.Error).IsSameReferenceAs(after);
        moving = EventStreamSteps.PendingNext(watcher);
        scenario.Feed.RecordConnected(source, SharedEventFeedScenario.Marker("M2"));
        await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(watcher.Current.Status).IsEqualTo(EventConnectionStatus.Connected);
        moving = EventStreamSteps.PendingNext(watcher);
        await Assert.That(moving.IsCompleted).IsFalse();
        scenario.Feed.RecordDrop(source, earlier, 4);
        await Assert.That(await moving.WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(watcher.Current.Attempt).IsEqualTo(4);
        await Assert.That(watcher.Current.Error).IsSameReferenceAs(earlier);
    }

    [Test]
    public async Task GetConnectionState_Should_Throw_ObjectDisposedException_After_Disposal()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var watcher = scenario.Feed.Watch(default).GetAsyncEnumerator();
        await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsTrue();
        var pending = EventStreamSteps.PendingNext(watcher);
        scenario.Feed.Dispose();

        _ = await Assert.That(scenario.Feed.GetConnectionState).Throws<ObjectDisposedException>();
        _ = await Assert.That(pending).Throws<ObjectDisposedException>();
        await Assert.That(scenario.Feed.WatcherCount).IsEqualTo(0);
        await watcher.DisposeAsync();
        await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsFalse();
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(0);
    }

    [Test]
    [Arguments(false, false)]
    [Arguments(false, true)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task WatchConnectionAsync_Should_Observe_The_Enumerator_Token_Without_Another_Pull(bool sameToken, bool cancelEnumerator)
    {
        using var caller = new CancellationTokenSource();
        using var enumeration = new CancellationTokenSource();
        await using var scenario = new SharedEventFeedScenario();
        await using var watcher = (EventStateWatcher)scenario.Feed.Watch(caller.Token)
            .GetAsyncEnumerator(sameToken ? caller.Token : enumeration.Token);
        await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(watcher.Current.Status).IsEqualTo(EventConnectionStatus.Idle);
        await Assert.That(watcher.RegistrationCount).IsEqualTo(sameToken ? 1 : 2);
        await Assert.That(scenario.Feed.WatcherCount).IsEqualTo(1);
        await (cancelEnumerator && !sameToken ? enumeration : caller).CancelOnWorkerAsync();

        await Assert.That(watcher.RegistrationCount).IsEqualTo(0);
        await Assert.That(scenario.Feed.WatcherCount).IsEqualTo(0);
        OperationCanceledException? canceled = null;
        try
        {
            _ = await watcher.MoveNextAsync();
        }
        catch (OperationCanceledException exception)
        {
            canceled = exception;
        }

        await Assert.That(canceled).IsNotNull();
        if (sameToken)
        {
            await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
        }

        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(0);
    }

    [Test]
    public async Task WatchConnectionAsync_Should_Dispose_Its_Own_Registration_From_The_Cancellation_Callback()
    {
        using var caller = new CancellationTokenSource();
        await using var scenario = new SharedEventFeedScenario();
        await using var watcher = (EventStateWatcher)scenario.Feed.Watch(caller.Token).GetAsyncEnumerator();
        await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsTrue();
        await DedicatedThread.Run(caller.Cancel).WaitAsync(HangGuard.Bound);

        await Assert.That(watcher.RegistrationCount).IsEqualTo(0);
        await Assert.That(scenario.Feed.WatcherCount).IsEqualTo(0);
        var canceled = await Assert.That(async () => await watcher.MoveNextAsync()).Throws<OperationCanceledException>();
        await Assert.That(canceled!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(0);
    }

    [Test]
    public async Task GetConnectionState_Should_Start_Idle_And_Not_Open_A_Source()
    {
        await using var scenario = new SharedEventFeedScenario();
        var initial = scenario.Feed.GetConnectionState();
        await Assert.That(initial.Status).IsEqualTo(EventConnectionStatus.Idle);
        await Assert.That(initial.Attempt).IsEqualTo(0);
        await Assert.That(initial.Error).IsNull();
        await using var watcher = (EventStateWatcher)scenario.Feed.Watch(default).GetAsyncEnumerator();
        await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(watcher.Current).IsSameReferenceAs(initial);
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(0);
        await using var events = await scenario.AdmitAsync("M");
        await events.DisposeAsync();
        await Assert.That(await EventStreamSteps.PendingNext(watcher).WaitAsync(HangGuard.Bound)).IsTrue();
        await Assert.That(watcher.Current.Status).IsEqualTo(EventConnectionStatus.Idle);
        await Assert.That(watcher.Current.Attempt).IsEqualTo(0);
        await Assert.That(watcher.Current.Error).IsNull();
        await Assert.That(scenario.Feed.GetCurrentSource()).IsNull();
        await Assert.That(scenario.Attempts.OpenCount).IsEqualTo(1);
    }
}
