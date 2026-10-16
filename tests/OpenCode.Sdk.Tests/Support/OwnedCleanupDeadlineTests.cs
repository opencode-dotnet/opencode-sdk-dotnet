using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness.Ownership;

namespace OpenCode.Sdk.Tests.Support;

public sealed class OwnedCleanupDeadlineTests
{
    [Test]
    [Arguments(false, false)]
    [Arguments(true, false)]
    [Arguments(true, true)]
    public async Task CompleteAsync_Should_Observe_Cancellation_After_The_Deadline_Wins_Before_Inspection(
        bool ownedToken, bool cancelCaller)
    {
        using var caller = new CancellationTokenSource();
        using var unrelated = new CancellationTokenSource();
        var scenario = new OperationDeadlineScenario();
        var deadline = scenario.Hold("event reader");
        var owner = new OwnedEventReader(TimeSpan.FromMinutes(1), TimeSpan.FromMilliseconds(50), caller.Token, scenario.Deadline);
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellation = new OperationCanceledException(ownedToken ? owner.Token : unrelated.Token);
        var primary = new InvalidOperationException("body failed");
        var reading = Task.Run(async () =>
        {
            _ = entered.TrySetResult(true);
            await release.Task;
            throw cancellation;
        }, CancellationToken.None);
        owner.Own(reading);
        var completing = Task.CompletedTask;
        try
        {
            await entered.Task;
            completing = owner.CompleteAsync(primary);
            await deadline.Entered;
            deadline.Expire();
            await deadline.Won;
            if (cancelCaller)
            {
                await caller.CancelOnWorkerAsync();
            }

            release.SetResult(true);
            var readerFailure = await Assert.That(() => reading).Throws<OperationCanceledException>();
            await Assert.That(readerFailure).IsSameReferenceAs(cancellation);
            await Assert.That(reading.IsCanceled).IsTrue();
            await Assert.That(completing.IsCompleted).IsFalse();
            deadline.Deliver();
            var thrown = await Assert.That(() => completing).Throws<InvalidOperationException>();
            await Assert.That(thrown).IsSameReferenceAs(primary);
            var immediate = (AggregateException)primary.Data[OwnedCleanup.FailuresKey]!;
            await Assert.That(immediate.InnerExceptions.Single().InnerException).IsSameReferenceAs(deadline.Failure);
            var report = owner.LateFailures!;
            await report.WaitForAllAsync(CancellationToken.None);
            var attached = (IReadOnlyCollection<KeyValuePair<string, Exception>>)primary.Data[OwnedCleanup.LateFailuresKey]!;
            if (ownedToken && !cancelCaller)
            {
                await Assert.That(attached).IsEmpty();
            }
            else
            {
                await Assert.That(attached).Count().IsEqualTo(1);
                await Assert.That(attached.Single().Key).IsEqualTo("event reader");
                await Assert.That(attached.Single().Value).IsSameReferenceAs(cancellation);
                await Assert.That(((OperationCanceledException)attached.Single().Value).CancellationToken)
                    .IsEqualTo(cancellation.CancellationToken);
            }
        }
        finally
        {
            _ = release.TrySetResult(true);
            await scenario.DrainAsync(Task.WhenAll(reading, completing));
            if (owner.LateFailures is { } report)
            {
                await report.WaitForAllAsync(CancellationToken.None);
            }
        }
    }

    [Test]
    [Arguments(false)]
    [Arguments(true)]
    public async Task CompleteAsync_Should_Observe_Terminal_Outcome_After_The_Deadline_Wins(bool fault)
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = new OperationDeadlineScenario();
        var deadline = scenario.Hold("owned read");
        var cleanup = new OwnedCleanup(TimeSpan.FromMilliseconds(50), scenario.Deadline);
        var failure = new InvalidOperationException("late read failure");
        cleanup.Own("owned read", pending.Task);
        var completing = cleanup.CompleteAsync(null);
        try
        {
            await deadline.Entered;
            deadline.Expire();
            await deadline.Won;
            if (fault)
            {
                pending.SetException(failure);
            }
            else
            {
                pending.SetResult(true);
            }

            await Assert.That(pending.Task.IsCompleted).IsTrue();
            deadline.Deliver();
            var thrown = await Assert.That(() => completing).Throws<TimeoutException>();
            await Assert.That(thrown!.Message).Contains("'owned read'");
            await Assert.That(thrown.InnerException).IsSameReferenceAs(deadline.Failure);
            var lateReport = cleanup.LateFailures;
            await Assert.That(lateReport).IsNotNull();
            await lateReport.WaitForAllAsync(CancellationToken.None);
            var attached = (IReadOnlyCollection<KeyValuePair<string, Exception>>)thrown.Data[OwnedCleanup.LateFailuresKey]!;
            if (fault)
            {
                await Assert.That(attached.Single().Value).IsSameReferenceAs(failure);
            }
            else
            {
                await Assert.That(attached).IsEmpty();
            }
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

    [Test]
    public async Task CompleteAsync_Should_Observe_An_Existing_Task_Without_Queuing_Invocation()
    {
        var scenario = new OperationDeadlineScenario();
        var pending = Task.CompletedTask;
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(1), scenario.Deadline);
        cleanup.Own("already running", pending);

        await cleanup.CompleteAsync(null);

        await scenario.Deadline.Received(1).WaitAsync("already running", pending, TimeSpan.FromSeconds(1), CancellationToken.None);
    }

    [Test]
    public async Task CompleteAsync_Should_Preserve_An_Operation_Timeout_Identity()
    {
        var cause = new InvalidOperationException("transport cause");
        var timeout = new TimeoutException("operation timed out", cause);
        var cleanup = new OwnedCleanup(TimeSpan.FromSeconds(1), new OperationDeadlineScenario().Deadline);
        cleanup.Own("read", Task.FromException(timeout));

        var thrown = await Assert.That(() => cleanup.CompleteAsync(null)).Throws<TimeoutException>();

        await Assert.That(thrown).IsSameReferenceAs(timeout);
        await Assert.That(thrown!.InnerException).IsSameReferenceAs(cause);
        await Assert.That(cleanup.LateFailures).IsNull();
    }

    [Test]
    public async Task CompleteAsync_Should_Bound_Synchronously_Blocked_Invocation()
    {
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = new OperationDeadlineScenario();
        var deadline = scenario.Hold("blocking invocation");
        var cleanup = new OwnedCleanup(TimeSpan.FromMilliseconds(50), scenario.Deadline);
        var late = new InvalidOperationException("invocation failed after release");
        cleanup.Own("blocking invocation", token =>
        {
            _ = entered.TrySetResult(true);
            release.Wait(CancellationToken.None);
            throw late;
        });
        var completing = cleanup.CompleteAsync(null);
        try
        {
            await entered.Task;
            await deadline.Entered;
            deadline.Expire();
            deadline.Deliver();
            _ = await Assert.That(() => completing).Throws<TimeoutException>();
            release.Set();
            var lateReport = cleanup.LateFailures;
            await Assert.That(lateReport).IsNotNull();
            await lateReport.WaitForAllAsync(CancellationToken.None);
            await Assert.That(lateReport.Failures.Single().Value).IsSameReferenceAs(late);
        }
        finally
        {
            release.Set();
            await scenario.DrainAsync(completing);
            if (cleanup.LateFailures is { } report)
            {
                await report.WaitForAllAsync(CancellationToken.None);
            }
        }
    }
}
