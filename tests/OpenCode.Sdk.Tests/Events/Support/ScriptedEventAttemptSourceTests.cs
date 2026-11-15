using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Events.Support;

public sealed class ScriptedEventAttemptSourceTests
{
    [Test]
    public async Task DisposeAsync_Should_Surface_A_Cleanup_Fault_After_A_Normal_End()
    {
        var source = new ScriptedEventAttemptSource();
        using var attempt = new StreamAttempt(new ManualTimeProvider(), EventStreamSteps.IdleWindow, default);
        var events = source.OpenAsync(attempt, default).GetAsyncEnumerator();
        var moving = events.MoveNextAsync().AsTask();
        var opened = await source.NextOpenAsync();
        var failure = new InvalidOperationException("cleanup failure");
        opened.CleanupFailure = failure;
        _ = opened.Items.Writer.TryComplete();
        opened.ReleaseCleanup();
        bool? moved = null;
        Exception? readFailure = null;
        Exception? cleanupFailure = null;
        try
        {
            try
            {
                moved = await moving.WaitAsync(HangGuard.Bound);
            }
            catch (Exception exception)
            {
                readFailure = exception;
            }
        }
        finally
        {
            try
            {
                await events.DisposeAsync();
            }
            catch (Exception exception)
            {
                cleanupFailure = exception;
            }
        }

        await Assert.That(readFailure).IsNull();
        await Assert.That(moved).IsFalse();
        await Assert.That(cleanupFailure).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task OpenAsync_Should_Acknowledge_Publication_And_Hold_Cleanup_Until_Released()
    {
        var source = new ScriptedEventAttemptSource();
        using var attempt = new StreamAttempt(new ManualTimeProvider(), EventStreamSteps.IdleWindow, default);
        var ledger = new List<string>();
        var received = new TaskCompletionSource<IEvent>(TaskCreationOptions.RunContinuationsAsynchronously);
        async Task ReadAsync()
        {
            await foreach (var item in source.OpenAsync(attempt, default))
            {
                received.SetResult(item);
            }
        }

        var reading = ReadAsync();
        var opened = await source.NextOpenAsync();
        try
        {
            ledger.Add("open");
            var publishing = opened.PublishAsync(SharedEventFeedScenario.Marker("M"));
            await Assert.That((await received.Task.WaitAsync(HangGuard.Bound)).Id).IsEqualTo("M");
            await publishing.WaitAsync(HangGuard.Bound);
            ledger.Add("publication");
            _ = opened.Items.Writer.TryComplete();
            var entered = opened.CleanupEntered.Task.ContinueWith(_ => ledger.Add("cleanup-entered"),
                CancellationToken.None, TaskContinuationOptions.None, TaskScheduler.Default);
            await entered.WaitAsync(HangGuard.Bound);
            await Assert.That(opened.CleanupReleased.Task.IsCompleted).IsFalse();
        }
        finally
        {
            _ = opened.Items.Writer.TryComplete();
            source.ReleaseCleanup();
            await reading.WaitAsync(HangGuard.Bound);
        }

        ledger.Add("cleanup-released");
        await Assert.That(opened.CleanupReleased.Task.Status).IsEqualTo(TaskStatus.RanToCompletion);
        await Assert.That(ledger)
            .IsEquivalentTo(["open", "publication", "cleanup-entered", "cleanup-released"], CollectionOrdering.Matching);
    }
}
