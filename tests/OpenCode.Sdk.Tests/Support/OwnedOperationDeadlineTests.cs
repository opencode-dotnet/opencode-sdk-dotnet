using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Harness.Ownership;
namespace OpenCode.Sdk.Tests.Support;

public sealed class OwnedOperationDeadlineTests
{
    [Test]
    public async Task WaitAsync_Should_Observe_Completed_Fault_Without_Confusing_It_With_The_Deadline()
    {
        var failure = new TimeoutException("operation timeout");
        var pending = Task.FromException(failure);
        var deadline = new OwnedOperationDeadline();

        await deadline.WaitAsync("read", pending, TimeSpan.Zero);

        await Assert.That(pending.Exception!.InnerException).IsSameReferenceAs(failure);
    }

    [Test]
    public async Task WaitAsync_Should_Bound_An_Unfinished_Operation_Without_Canceling_It()
    {
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new OwnedOperationDeadline();
        try
        {
            _ = await Assert.That(() => deadline.WaitAsync("read", pending.Task, TimeSpan.Zero))
                .Throws<TimeoutException>();
            await Assert.That(pending.Task.IsCompleted).IsFalse();
        }
        finally
        {
            _ = pending.TrySetResult(true);
            _ = await pending.Task;
        }
    }

    [Test]
    public async Task WaitAsync_Should_Preserve_Caller_Cancellation()
    {
        using var caller = new CancellationTokenSource();
        await caller.CancelOnWorkerAsync();
        var pending = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var deadline = new OwnedOperationDeadline();
        try
        {
            var thrown = await Assert.That(() => deadline.WaitAsync("read", pending.Task, TimeSpan.FromSeconds(1), caller.Token))
                .Throws<OperationCanceledException>();
            await Assert.That(thrown!.CancellationToken).IsEqualTo(caller.Token);
            await Assert.That(pending.Task.IsCompleted).IsFalse();
        }
        finally
        {
            _ = pending.TrySetResult(true);
            _ = await pending.Task;
        }
    }
}
