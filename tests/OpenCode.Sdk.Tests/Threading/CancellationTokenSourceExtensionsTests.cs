using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Threading;

public sealed class CancellationTokenSourceExtensionsTests
{
    [Test]
    public async Task CancelOnWorkerAsync_Should_Return_Without_Waiting_And_Complete_Only_After_Callbacks()
    {
        using var source = new CancellationTokenSource();
        using var release = new ManualResetEventSlim();
        var entered = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
        var finished = false;
        using var registration = source.Token.Register(() =>
        {
            _ = entered.TrySetResult(true);
            release.Wait();
            finished = true;
        });
        // Publish the cancellation task as data: unwrapping it would hide a blocking invocation.
        var invocation = Task.Factory.StartNew(source.CancelOnWorkerAsync,
            CancellationToken.None, TaskCreationOptions.DenyChildAttach, TaskScheduler.Default);
        try
        {
            await entered.Task.WaitAsync(HangGuard.Bound);
            var cancellation = await invocation.WaitAsync(HangGuard.Bound);
            await Assert.That(cancellation.IsCompleted).IsFalse();
            release.Set();
            await cancellation.WaitAsync(HangGuard.Bound);
            await Assert.That(finished).IsTrue();
        }
        finally
        {
            release.Set();
            var cancellation = await invocation.WaitAsync(HangGuard.Bound);
            await cancellation.WaitAsync(HangGuard.Bound);
        }
    }
}
