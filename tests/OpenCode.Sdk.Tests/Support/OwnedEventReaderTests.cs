using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness.Ownership;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Support;

public sealed class OwnedEventReaderTests
{
    [Test]
    public async Task CompleteAsync_Should_Preserve_Primary_Reader_And_Later_Session_Failures()
    {
        var owner = new OwnedEventReader(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1), CancellationToken.None, new OperationDeadlineScenario().Deadline);
        var primary = new InvalidOperationException("prompt failed");
        var readerFailure = new InvalidOperationException("reader failed");
        var removalFailure = new InvalidOperationException("removal failed");
        owner.Own(Task.FromException(readerFailure));

        var thrown = await Assert.That(() => owner.CompleteAsync(primary)).Throws<InvalidOperationException>();
        var outer = new OwnedSessionCleanup(_ => Task.CompletedTask,
            _ => Task.FromException(removalFailure), TimeSpan.FromSeconds(1), new OperationDeadlineScenario().Deadline);
        _ = await Assert.That(() => outer.CompleteAsync(thrown)).Throws<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(primary);
        var failures = (AggregateException)primary.Data[OwnedCleanup.FailuresKey]!;
        await Assert.That(failures.InnerExceptions).IsEquivalentTo(new Exception[] { readerFailure, removalFailure }, CollectionOrdering.Matching);
    }

    [Test]
    public async Task CompleteAsync_Should_Observe_Reader_After_Cancellation_Callback_Fails()
    {
        var owner = new OwnedEventReader(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1), CancellationToken.None, new OperationDeadlineScenario().Deadline);
        var callbackFailure = new InvalidOperationException("cancellation callback failed");
        var readerFailure = new InvalidOperationException("reader also failed");
        var primary = new InvalidOperationException("prompt failed");
        using var registration = owner.Token.Register(() => throw callbackFailure);
        owner.Own(Task.FromException(readerFailure));

        var thrown = await Assert.That(() => owner.CompleteAsync(primary)).Throws<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(primary);
        var failures = ((AggregateException)primary.Data[OwnedCleanup.FailuresKey]!).Flatten().InnerExceptions;
        await Assert.That(failures).Contains(callbackFailure);
        await Assert.That(failures).Contains(readerFailure);
    }

    [Test]
    public async Task CompleteAsync_Should_Bound_Stalls_Reach_Removal_And_Attach_Late_Failures()
    {
        using var release = new ManualResetEventSlim();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var cancellationEntered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = new OperationDeadlineScenario();
        var cancellationDeadline = scenario.Hold("event cancellation");
        var readerDeadline = scenario.Hold("event reader");
        var owner = new OwnedEventReader(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(50), CancellationToken.None, scenario.Deadline);
        var callbackFailure = new InvalidOperationException("late callback failed");
        var readerFailure = new InvalidOperationException("late reader failed");
        var primary = new InvalidOperationException("prompt failed");
        using var registration = owner.Token.Register(() =>
        {
            _ = cancellationEntered.TrySetResult(true);
            release.Wait();
            throw callbackFailure;
        });
        owner.Own(pending.Task);
        var removed = false;
        var outer = new OwnedSessionCleanup(_ => Task.CompletedTask, token =>
        {
            removed = !token.IsCancellationRequested;
            return Task.CompletedTask;
        }, TimeSpan.FromSeconds(1), new OperationDeadlineScenario().Deadline);
        var completing = owner.CompleteAsync(primary);
        try
        {
            await cancellationEntered.Task;
            await cancellationDeadline.Entered;
            cancellationDeadline.Expire();
            cancellationDeadline.Deliver();
            await readerDeadline.Entered;
            readerDeadline.Expire();
            readerDeadline.Deliver();
            var thrown = await Assert.That(() => completing).Throws<InvalidOperationException>();
            _ = await Assert.That(() => outer.CompleteAsync(thrown)).Throws<InvalidOperationException>();
            await Assert.That(removed).IsTrue();
            await Assert.That(thrown).IsSameReferenceAs(primary);
            var failures = (AggregateException)primary.Data[OwnedCleanup.FailuresKey]!;
            await Assert.That(failures.InnerExceptions.OfType<TimeoutException>()).Count().IsEqualTo(2);
            await Assert.That(failures.InnerExceptions[0].Message).Contains("'event cancellation'");
            await Assert.That(failures.InnerExceptions[1].Message).Contains("'event reader'");
            var attached = (IReadOnlyCollection<KeyValuePair<string, Exception>>)primary.Data[OwnedCleanup.LateFailuresKey]!;
            pending.SetException(readerFailure);
            var report = owner.LateFailures;
            await Assert.That(report).IsNotNull();
            var observed = await report.WaitForFailureAsync(CancellationToken.None);
            await Assert.That(observed.Value).IsSameReferenceAs(readerFailure);
            release.Set();
            await report.WaitForAllAsync(CancellationToken.None);
            // Late failure completion order is not part of this assertion's contract.
            await Assert.That(attached.Select(entry => entry.Value)).IsEquivalentTo(new Exception[] { readerFailure, callbackFailure }, CollectionOrdering.Any);
            // Late failure completion order is not part of this assertion's contract.
            await Assert.That(attached.Select(entry => entry.Key)).IsEquivalentTo(["event reader", "event cancellation"], CollectionOrdering.Any);
        }
        finally
        {
            release.Set();
            _ = pending.TrySetResult(true);
            await scenario.DrainAsync(completing);
            if (owner.LateFailures is { } report)
            {
                await report.WaitForAllAsync(CancellationToken.None);
            }
        }
    }

    [Test]
    public async Task CompleteAsync_Should_Accept_Only_Its_Own_Cooperative_Reader_Cancellation()
    {
        var owner = new OwnedEventReader(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1), CancellationToken.None, new OperationDeadlineScenario().Deadline);
        var cancellation = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var token = owner.Token;
        using var registration = token.Register(() => _ = cancellation.TrySetCanceled(token));
        Task pending = cancellation.Task;
        owner.Own(pending);

        await owner.CompleteAsync(null);

        OperationCanceledException? cancelled = null;
        try
        {
            await pending;
        }
        catch (OperationCanceledException exception)
        {
            cancelled = exception;
        }

        await Assert.That(cancelled).IsNotNull();
        await Assert.That(owner.LateFailures).IsNull();
    }

    [Test]
    public async Task CompleteAsync_Should_Preserve_Caller_Cancellation_Identity()
    {
        using var caller = new CancellationTokenSource();
        var owner = new OwnedEventReader(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1), caller.Token, new OperationDeadlineScenario().Deadline);
        await caller.CancelOnWorkerAsync();
        var primary = new OperationCanceledException(caller.Token);
        owner.Own(Task.FromException(primary));

        var thrown = await Assert.That(() => owner.CompleteAsync(primary))
            .Throws<OperationCanceledException>();

        await Assert.That(thrown).IsSameReferenceAs(primary);
        await Assert.That(thrown!.CancellationToken).IsEqualTo(caller.Token);
        await Assert.That(primary.Data.Keys.Cast<object>()).DoesNotContain(OwnedCleanup.FailuresKey);
    }

    [Test]
    public async Task CompleteAsync_Should_Report_Unexpected_Reader_Cancellation()
    {
        var owner = new OwnedEventReader(Timeout.InfiniteTimeSpan, TimeSpan.FromSeconds(1), CancellationToken.None, new OperationDeadlineScenario().Deadline);
        var unexpected = new OperationCanceledException("unrelated cancellation");
        owner.Own(Task.FromException(unexpected));
        var primary = new InvalidOperationException("prompt failed");

        var thrown = await Assert.That(() => owner.CompleteAsync(primary))
            .Throws<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(primary);
        var failures = (AggregateException)primary.Data[OwnedCleanup.FailuresKey]!;
        await Assert.That(failures.InnerExceptions.Single()).IsSameReferenceAs(unexpected);
    }

    [Test]
    public async Task CompleteAsync_Should_Observe_Late_Cooperative_Cancellation_After_The_Reader_Deadline()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var scenario = new OperationDeadlineScenario();
        var deadline = scenario.Hold("event reader");
        var owner = new OwnedEventReader(Timeout.InfiniteTimeSpan, TimeSpan.FromMilliseconds(50), CancellationToken.None, scenario.Deadline);
        owner.Own(pending.Task);
        var token = owner.Token;
        var completing = owner.CompleteAsync(null);
        try
        {
            await deadline.Entered;
            await Assert.That(token.IsCancellationRequested).IsTrue();
            deadline.Expire();
            deadline.Deliver();
            var thrown = await Assert.That(() => completing).Throws<TimeoutException>();
            await Assert.That(thrown!.Message).Contains("'event reader'");
            var report = owner.LateFailures;
            await Assert.That(report).IsNotNull();

            pending.SetCanceled(token);
            await report.WaitForAllAsync(CancellationToken.None);

            await Assert.That(report.Failures).IsEmpty();
        }
        finally
        {
            _ = pending.TrySetResult(true);
            await scenario.DrainAsync(completing);
            if (owner.LateFailures is { } report)
            {
                await report.WaitForAllAsync(CancellationToken.None);
            }
        }
    }
}
