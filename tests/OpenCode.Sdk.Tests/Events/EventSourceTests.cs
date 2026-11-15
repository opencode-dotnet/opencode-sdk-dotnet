using OpenCode.Sdk.Tests.Events.Support;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Events;

public sealed class EventSourceTests
{
    [Test]
    public async Task RecordFault_Should_Observe_A_Retired_Cancel_Failure_Without_Changing_The_Replacement()
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var first = await scenario.AdmitAsync("M1");
        var source = scenario.Feed.GetCurrentSource()!;
        var oldAttempt = scenario.CurrentAttempt!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var failure = new InvalidOperationException("retired cancellation callback");
        using var registration = source.Token.UnsafeRegister(_ =>
        {
            entered.SetResult();
            if (!release.Wait(HangGuard.Bound))
            {
                throw new TimeoutException("The test did not release retired cancellation.");
            }

            throw failure;
        }, state: null);
        try
        {
            await first.DisposeAsync();
            await entered.Task.WaitAsync(HangGuard.Bound);
            await using var replacement = await scenario.AdmitAsync("M2");
            var current = scenario.Feed.GetCurrentSource();
            _ = oldAttempt.Items.Writer.TryComplete();
            oldAttempt.ReleaseCleanup();
            await EventStreamSteps.ObserveSourceCompletionAsync(source);
            await Assert.That(source.IsReleased).IsFalse();
            release.Set();
            await EventStreamSteps.ObserveSourceCompletionAsync(source, cancellation: true);

            Exception[] expected = [failure];
            await Assert.That(source.CancellationFailure!.InnerExceptions).IsEquivalentTo(expected, CollectionOrdering.Matching);
            await Assert.That(source.IsReleased).IsTrue();
            await Assert.That(scenario.Feed.GetCurrentSource()).IsSameReferenceAs(current);
            await scenario.PublishAsync("E2");
            await Assert.That((await EventStreamSteps.NextAsync(replacement)).Id).IsEqualTo("E2");
        }
        finally
        {
            release.Set();
            oldAttempt.ReleaseCleanup();
            if (source.CancellationObservation is { } observing)
            {
                await observing.WaitAsync(HangGuard.Bound);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task RequestStop_Should_Observe_Runner_And_Worker_Cancellation_Before_Releasing_The_Cts(bool failCancellation)
    {
        await using var scenario = new SharedEventFeedScenario();
        await using var events = await scenario.AdmitAsync("M");
        var attempt = scenario.CurrentAttempt!;
        var source = scenario.Feed.GetCurrentSource()!;
        var entered = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        using var release = new ManualResetEventSlim();
        var failure = new InvalidOperationException("cancel callback");
        using var registration = source.Token.UnsafeRegister(_ =>
        {
            entered.SetResult();
            if (!release.Wait(HangGuard.Bound))
            {
                throw new TimeoutException("The test did not release the cancellation callback.");
            }

            if (failCancellation)
            {
                throw failure;
            }
        }, state: null);

        try
        {
            await events.DisposeAsync();
            await entered.Task.WaitAsync(HangGuard.Bound);
            _ = attempt.Items.Writer.TryComplete();
            attempt.ReleaseCleanup();
            await EventStreamSteps.ObserveSourceCompletionAsync(source);

            await Assert.That(source.IsReleased).IsFalse();
            await Assert.That(source.Token.IsCancellationRequested).IsTrue();
        }
        finally
        {
            release.Set();
            if (source.CancellationObservation is { } observing)
            {
                await observing.WaitAsync(HangGuard.Bound);
            }
        }

        await Assert.That(source.CancellationObservation).IsNotNull();
        await scenario.Feed.JoinAsync().WaitAsync(HangGuard.Bound);
        await Assert.That(source.IsReleased).IsTrue();
        if (failCancellation)
        {
            Exception[] expected = [failure];
            await Assert.That(source.CancellationFailure!.InnerExceptions).IsEquivalentTo(expected, CollectionOrdering.Matching);
        }
        else
        {
            await Assert.That(source.CancellationFailure).IsNull();
        }
    }
}
