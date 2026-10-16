using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Harness.Ownership;

namespace OpenCode.Sdk.Tests.Support;

public sealed class OwnedSessionInboxCleanupTests
{
    private static readonly TimeSpan CleanupTimeout = TimeSpan.FromSeconds(1);

    [Test]
    public async Task CompleteAsync_Should_Observe_The_Retained_Wait_After_Interrupt_And_Preserve_Its_Fault()
    {
        var primary = new InvalidOperationException("body failed after wait initiation");
        var waitFailure = new InvalidOperationException("original wait failed");
        var freshWaitFailure = new InvalidOperationException("fresh wait failed");
        var removalFailure = new InvalidOperationException("removal failed");
        var disconnectFailure = new InvalidOperationException("drive disconnect failed");
        var wait = new TaskCompletionSource<ExperimentalSessionWaitResponse>(TaskCreationOptions.RunContinuationsAsynchronously);
        var operations = new List<string>();
        var cleanup = CreateCleanup(
            _ =>
            {
                operations.Add("interrupt");
                wait.SetException(waitFailure);
                return Task.CompletedTask;
            },
            _ =>
            {
                operations.Add("fresh wait");
                return Task.FromException(freshWaitFailure);
            },
            _ =>
            {
                operations.Add("removal");
                return Task.FromException(removalFailure);
            },
            invocation => new OwnedDriveInvocation(
                invocation,
                (_, _) => Task.CompletedTask,
                _ => Task.CompletedTask,
                _ =>
                {
                    operations.Add("drive disconnect");
                    return Task.FromException(disconnectFailure);
                }));
        _ = cleanup.RetainInvocation(new DriveInvocation("unfinished", "https://example.test", "sim", default));
        cleanup.RetainWait(wait.Task);

        var thrown = await Assert.That(() => cleanup.CompleteAsync(primary)).Throws<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(primary);
        await Assert.That(primary.Data[OwnedCleanup.FailuresKey]).IsTypeOf<AggregateException>();
        var failures = (AggregateException)primary.Data[OwnedCleanup.FailuresKey]!;
        await Assert.That(failures.InnerExceptions).Count().IsEqualTo(4);
        await Assert.That(failures.InnerExceptions[0]).IsSameReferenceAs(waitFailure);
        await Assert.That(failures.InnerExceptions[1]).IsSameReferenceAs(freshWaitFailure);
        await Assert.That(failures.InnerExceptions[2]).IsSameReferenceAs(removalFailure);
        await Assert.That(failures.InnerExceptions[3]).IsSameReferenceAs(disconnectFailure);
        await Assert.That(operations).Count().IsEqualTo(4);
        await Assert.That(operations[0]).IsEqualTo("interrupt");
        await Assert.That(operations[1]).IsEqualTo("fresh wait");
        await Assert.That(operations[2]).IsEqualTo("removal");
        await Assert.That(operations[3]).IsEqualTo("drive disconnect");
    }

    [Test]
    public async Task CompleteAsync_Should_Disconnect_Only_Unfinished_Invocations_After_Session_Cleanup_Fails()
    {
        var interruptFailure = new InvalidOperationException("interrupt failed");
        var disconnected = new List<string>();
        var cleanup = CreateCleanup(
            _ => Task.FromException(interruptFailure),
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            invocation => CreateInvocation(invocation, disconnected));
        var finished = cleanup.RetainInvocation(new DriveInvocation("finished", "https://example.test", "sim", default));
        _ = cleanup.RetainInvocation(new DriveInvocation("unfinished", "https://example.test", "sim", default));
        await finished.FinishAsync();

        var thrown = await Assert.That(() => cleanup.CompleteAsync(null)).Throws<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(interruptFailure);
        await Assert.That(disconnected).Count().IsEqualTo(1);
        await Assert.That(disconnected[0]).IsEqualTo("unfinished");
    }

    [Test]
    public async Task CompleteAsync_Should_Not_Disconnect_An_Invocation_For_A_Primary_Failure_Alone()
    {
        var primary = new InvalidOperationException("body failed");
        var disconnected = new List<string>();
        var cleanup = CreateCleanup(
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            _ => Task.CompletedTask,
            invocation => CreateInvocation(invocation, disconnected));
        _ = cleanup.RetainInvocation(new DriveInvocation("unfinished", "https://example.test", "sim", default));

        var thrown = await Assert.That(() => cleanup.CompleteAsync(primary)).Throws<InvalidOperationException>();

        await Assert.That(thrown).IsSameReferenceAs(primary);
        await Assert.That(disconnected).IsEmpty();
    }

    private static OwnedSessionInboxCleanup CreateCleanup(
        Func<CancellationToken, Task> interrupt,
        Func<CancellationToken, Task> wait,
        Func<CancellationToken, Task> remove,
        Func<DriveInvocation, OwnedDriveInvocation>? invocationOwner = null)
    {
        var deadline = new OperationDeadlineScenario().Deadline;
        return new OwnedSessionInboxCleanup(
            interrupt,
            wait,
            remove,
            invocationOwner ?? (invocation => CreateInvocation(invocation, [])),
            CleanupTimeout,
            deadline);
    }

    private static OwnedDriveInvocation CreateInvocation(DriveInvocation invocation, List<string> disconnected) =>
        new(
            invocation,
            (_, _) => Task.CompletedTask,
            _ => Task.CompletedTask,
            id =>
            {
                disconnected.Add(id);
                return Task.CompletedTask;
            });
}
