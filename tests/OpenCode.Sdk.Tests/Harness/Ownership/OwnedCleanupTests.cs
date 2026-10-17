using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Harness.Ownership;

public sealed class OwnedCleanupTests
{
    [Test]
    public async Task CompleteAsync_Should_Retain_Every_Task_Fault_Without_Duplicating_The_Primary()
    {
        var primary = new InvalidOperationException("primary failed");
        var secondary = new InvalidOperationException("secondary failed");
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(1), new OperationDeadlineScenario().Deadline);
        cleanup.Own("reader", Task.WhenAll(Task.FromException(primary), Task.FromException(secondary)));

        var thrown = await Assert.That(() => cleanup.CompleteAsync(primary)).Throws<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(primary);
        var failures = (AggregateException)primary.Data[OwnedCleanup.FailuresKey]!;
        await Assert.That(failures.InnerExceptions.Single()).IsSameReferenceAs(secondary);
        await Assert.That(cleanup.OperationFailures).Count().IsEqualTo(2);
        await Assert.That(cleanup.OperationFailures[0]).IsSameReferenceAs(primary);
        await Assert.That(cleanup.OperationFailures[1]).IsSameReferenceAs(secondary);
    }

    [Test]
    public async Task CompleteAsync_Should_Append_Nested_Late_Failures_To_The_Attached_Queue()
    {
        var first = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var second = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = new OperationDeadlineScenario();
        var innerDeadline = scenario.Hold("inner read");
        var outerDeadline = scenario.Hold("outer removal");
        var inner = new OwnedCleanup(TimeSpan.FromMilliseconds(50), scenario.Deadline);
        var outer = new OwnedCleanup(TimeSpan.FromMilliseconds(50), scenario.Deadline);
        inner.Own("inner read", first.Task);
        outer.Own("outer removal", second.Task);
        var primary = new InvalidOperationException("primary failed");
        var firstFailure = new InvalidOperationException("inner late failure");
        var secondFailure = new InvalidOperationException("outer late failure");
        var innerCompletion = inner.CompleteAsync(primary);
        var outerCompletion = Task.CompletedTask;
        try
        {
            await innerDeadline.Entered;
            innerDeadline.Expire();
            innerDeadline.Deliver();
            _ = await Assert.That(() => innerCompletion).Throws<InvalidOperationException>();
            var attached = primary.Data[OwnedCleanup.LateFailuresKey];
            outerCompletion = outer.CompleteAsync(primary);
            await outerDeadline.Entered;
            outerDeadline.Expire();
            outerDeadline.Deliver();
            _ = await Assert.That(() => outerCompletion).Throws<InvalidOperationException>();
            await Assert.That(primary.Data[OwnedCleanup.LateFailuresKey]).IsSameReferenceAs(attached);
            var immediate = (AggregateException)primary.Data[OwnedCleanup.FailuresKey]!;
            await Assert.That(immediate.InnerExceptions).Count().IsEqualTo(2);
            first.SetException(firstFailure);
            second.SetException(secondFailure);
            var innerLate = inner.LateFailures;
            var outerLate = outer.LateFailures;
            await Assert.That(innerLate).IsNotNull();
            await Assert.That(outerLate).IsNotNull();
            await innerLate.WaitForAllAsync(CancellationToken.None);
            await outerLate.WaitForAllAsync(CancellationToken.None);

            var failures = (IReadOnlyCollection<KeyValuePair<string, Exception>>)attached!;
            // Late failure completion order is not part of this assertion's contract.
            await Assert.That(failures.Select(entry => entry.Value))
                .IsEquivalentTo(new Exception[] { firstFailure, secondFailure }, CollectionOrdering.Any);
        }
        finally
        {
            _ = first.TrySetResult(true);
            _ = second.TrySetResult(true);
            await scenario.DrainAsync(Task.WhenAll(innerCompletion, outerCompletion));
            if (inner.LateFailures is { } innerReport)
            {
                await innerReport.WaitForAllAsync(CancellationToken.None);
            }

            if (outer.LateFailures is { } outerReport)
            {
                await outerReport.WaitForAllAsync(CancellationToken.None);
            }
        }
    }

    [Test]
    public async Task CompleteAsync_Should_Bound_An_Operation_And_Its_Stalled_Cancellation_Independently()
    {
        using var release = new ManualResetEventSlim();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var primary = new InvalidOperationException("body failed");
        var late = new InvalidOperationException("late operation failed");
        var scenario = new OperationDeadlineScenario();
        var operationDeadline = scenario.Hold("stalled operation");
        var cancellationDeadline = scenario.Hold("stalled operation cancellation");
        var cleanup = new OwnedCleanup(TimeSpan.FromMilliseconds(50), scenario.Deadline);
        var laterTokenCancelled = true;
        cleanup.Own("stalled operation", async token =>
        {
            using var registration = token.Register(() =>
            {
                _ = cancellationEntered.TrySetResult(true);
                release.Wait(CancellationToken.None);
            });
            _ = entered.TrySetResult(true);
            _ = await pending.Task;
        });
        cleanup.Own("later cleanup", token =>
        {
            laterTokenCancelled = token.IsCancellationRequested;
            return Task.CompletedTask;
        });
        var completing = cleanup.CompleteAsync(primary);
        try
        {
            await entered.Task;
            await operationDeadline.Entered;
            operationDeadline.Expire();
            operationDeadline.Deliver();
            await cancellationEntered.Task;
            await cancellationDeadline.Entered;
            cancellationDeadline.Expire();
            cancellationDeadline.Deliver();
            var thrown = await Assert.That(() => completing).Throws<InvalidOperationException>();

            await Assert.That(thrown).IsSameReferenceAs(primary);
            await Assert.That(laterTokenCancelled).IsFalse();
            var failures = (AggregateException)primary.Data[OwnedCleanup.FailuresKey]!;
            await Assert.That(failures.InnerExceptions).Count().IsEqualTo(2);
            await Assert.That(failures.InnerExceptions[0].Message).Contains("'stalled operation'");
            await Assert.That(failures.InnerExceptions[0].InnerException).IsSameReferenceAs(operationDeadline.Failure);
            await Assert.That(failures.InnerExceptions[1].Message).Contains("'stalled operation cancellation'");
            await Assert.That(failures.InnerExceptions[1].InnerException).IsSameReferenceAs(cancellationDeadline.Failure);
            release.Set();
            pending.SetException(late);
            var lateReport = cleanup.LateFailures;
            await Assert.That(lateReport).IsNotNull();
            await lateReport.WaitForAllAsync(CancellationToken.None);
            var attached = (IReadOnlyCollection<KeyValuePair<string, Exception>>)primary.Data[OwnedCleanup.LateFailuresKey]!;
            await Assert.That(attached.Single().Value).IsSameReferenceAs(late);
        }
        finally
        {
            release.Set();
            _ = pending.TrySetResult(true);
            await scenario.DrainAsync(completing);
            if (cleanup.LateFailures is { } report)
            {
                await report.WaitForAllAsync(CancellationToken.None);
            }
        }
    }

    [Test]
    public async Task CompleteAsync_Should_Name_A_Steps_Own_Budget_And_The_Steps_That_Ran_Before_It()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = new OperationDeadlineScenario();
        var deadline = scenario.Hold("budgeted step");
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(15), scenario.Deadline);
        cleanup.Own("first step", _ => Task.CompletedTask);
        cleanup.Own("second step", _ => Task.CompletedTask);
        cleanup.Own("budgeted step", TimeSpan.FromSeconds(65), async token => _ = await pending.Task.WaitAsync(token));
        cleanup.Own("later step", _ => Task.CompletedTask);
        var completing = cleanup.CompleteAsync(null);
        try
        {
            await deadline.Entered;
            deadline.Expire();
            deadline.Deliver();
            var thrown = await Assert.That(() => completing).Throws<TimeoutException>();

            await Assert.That(thrown).IsNotNull();
            await Assert.That(thrown.Message).Contains("'budgeted step' exceeded its cleanup deadline of 00:01:05.");
            await Assert.That(thrown.Message).Contains("Steps before it: 'first step' ");
            await Assert.That(thrown.Message).Contains(", 'second step' ");
            await Assert.That(thrown.InnerException).IsSameReferenceAs(deadline.Failure);
            var timeline = thrown.Data[OwnedCleanup.StepTimingsKey] as string ?? string.Empty;
            var first = timeline.IndexOf("'first step' ", StringComparison.Ordinal);
            var budgeted = timeline.IndexOf("'budgeted step' ", StringComparison.Ordinal);
            var later = timeline.IndexOf("'later step' ", StringComparison.Ordinal);
            await Assert.That(first).IsGreaterThanOrEqualTo(0);
            await Assert.That(budgeted).IsGreaterThan(first);
            await Assert.That(later).IsGreaterThan(budgeted);
            await Assert.That(timeline).Contains("'budgeted step' exceeded 00:01:05");
        }
        finally
        {
            _ = pending.TrySetResult(true);
            await scenario.DrainAsync(completing);
            if (cleanup.LateFailures is { } report)
            {
                await report.WaitForAllAsync(CancellationToken.None);
            }
        }
    }
}
