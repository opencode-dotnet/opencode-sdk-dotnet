using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Events.Support;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Events;

public sealed class SharedEventFeedTests
{
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
        scenario.Feed.RecordConnected(oldSource, SharedEventFeedScenario.Marker("stale"));
        scenario.Feed.Publish(oldSource, SharedEventFeedScenario.Event("stale-event"));
        scenario.Feed.RecordDrop(oldSource);
        scenario.Feed.RecordFault(oldSource, new InvalidOperationException("stale fault"));
        oldAttempt.ReleaseCleanup();
        await using var late = await scenario.AdmitAsync("M2");

        await Assert.That(late.Current).IsSameReferenceAs(marker);
        await Assert.That(scenario.Feed.GetCurrentSource()).IsSameReferenceAs(source);
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
