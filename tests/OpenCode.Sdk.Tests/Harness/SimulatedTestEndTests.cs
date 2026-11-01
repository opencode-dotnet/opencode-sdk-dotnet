using NSubstitute;
using OpenCode.Sdk.Tests.Harness.Abstractions;
using OpenCode.Sdk.Tests.Harness.Ownership.Abstractions;
using OpenCode.Sdk.Tests.Support;
using TUnit.Assertions.Enums;

namespace OpenCode.Sdk.Tests.Harness;

public sealed class SimulatedTestEndTests
{
    private readonly IOwnedOperationDeadline _deadline = new OperationDeadlineScenario().Deadline;

    [Test]
    public async Task CompleteAsync_Should_Evict_And_Keep_The_Check_Failure_When_The_Check_Throws()
    {
        var check = new InvalidOperationException();
        var calls = new List<string>();
        var thrown = await Assert.That(() => CompleteAsync(
            () =>
            {
                calls.Add("check");
                return Task.FromException(check);
            }, _ =>
            {
                calls.Add("evict");
                return Task.CompletedTask;
            }, false, ReportUnexpectedFailure)).Throws<InvalidOperationException>();
        await Assert.That(thrown).IsSameReferenceAs(check);
        await Assert.That(calls).IsEquivalentTo(["check", "evict"], CollectionOrdering.Matching);
    }

    [Test]
    public async Task CompleteAsync_Should_Throw_The_Eviction_Failure_When_The_Test_Passed()
    {
        var failure = new InvalidOperationException();
        var thrown = await Assert.That(() => CompleteAsync(
            () => Task.CompletedTask, _ => throw failure, false, ReportUnexpectedFailure)).Throws<InvalidOperationException>();
        await Assert.That(thrown).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task CompleteAsync_Should_Report_The_Eviction_Failure_When_The_Test_Failed()
    {
        var failure = new FormatException();
        var reported = new List<Exception>();
        await CompleteAsync(() => Task.CompletedTask, _ => Task.FromException(failure), true, reported.Add);
        await Assert.That(reported).Count().IsEqualTo(1);
        await Assert.That(reported[0]).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task CompleteAsync_Should_Report_The_Eviction_Failure_When_The_Check_Threw()
    {
        var check = new InvalidOperationException();
        var eviction = new FormatException();
        var reported = new List<Exception>();
        var thrown = await Assert.That(() => CompleteAsync(
            () => throw check, _ => Task.FromException(eviction), false, reported.Add)).Throws<InvalidOperationException>();
        await Assert.That(thrown).IsSameReferenceAs(check);
        await Assert.That(reported).Count().IsEqualTo(1);
        await Assert.That(reported[0]).IsSameReferenceAs(eviction);
    }

    [Test]
    public async Task CompleteAsync_Should_Cancel_The_Eviction_When_The_Deadline_Fires()
    {
        var expired = new TimeoutException();
        _deadline.WaitAsync(Arg.Any<string>(), Arg.Any<Task>(), Arg.Any<TimeSpan>(), Arg.Any<CancellationToken>())
            .Returns(Task.FromException(expired), Task.CompletedTask);
        var received = CancellationToken.None;
        Task? eviction = null;
        var registration = default(CancellationTokenRegistration);
        var thrown = await Assert.That(() => CompleteAsync(
            () => Task.CompletedTask, token =>
            {
                var settled = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
                received = token;
                registration = token.Register(() => settled.SetCanceled());
                eviction = settled.Task;
                return settled.Task;
            }, false, ReportUnexpectedFailure)).Throws<TimeoutException>();
        using (registration)
        {
            await Assert.That(thrown).IsSameReferenceAs(expired);
            await Assert.That(received.IsCancellationRequested).IsTrue();
            await Assert.That(eviction!.IsCompleted).IsTrue();
            await _deadline.Received(2).WaitAsync("finished location eviction", eviction, TimeSpan.FromSeconds(1), CancellationToken.None);
        }
    }

    private Task CompleteAsync(Func<Task> ensure, Func<CancellationToken, Task> evict, bool testFailed, Action<Exception> report) =>
        new SimulatedTestEnd(_deadline, TimeSpan.FromSeconds(1)).CompleteAsync(
            new TestEndWork(ensure, evict, report), testFailed, CancellationToken.None);

    private static void ReportUnexpectedFailure(Exception exception) =>
        throw new InvalidOperationException("Unexpected eviction report.", exception);

    private sealed class TestEndWork(Func<Task> ensure, Func<CancellationToken, Task> evict, Action<Exception> report) : ITestEndWork
    {
        public Task CheckNoInvocationLeftPendingAsync() => ensure();

        public Task EvictFinishedLocationsAsync(CancellationToken cancellationToken) => evict(cancellationToken);

        public void ReportEvictionFailure(Exception exception) => report(exception);
    }
}
