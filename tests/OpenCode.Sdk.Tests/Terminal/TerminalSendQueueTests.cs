using NSubstitute;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Terminal;

public sealed class TerminalSendQueueTests
{
    [Test]
    public async Task CloseAsync_Should_Declare_A_Reasoned_Best_Effort_Close_Suppression()
    {
        var close = typeof(TerminalSendQueue).GetMethod(nameof(TerminalSendQueue.CloseAsync));
        var suppressions = SuppressionDeclaration.BestEffortFailures(close!);

        await Assert.That(suppressions).Count().IsEqualTo(1);
        await Assert.That(suppressions.Single().Reason).IsEqualTo(
            "Expected graceful-close failure is superseded by the owner's unconditional socket teardown " +
            "and receiver/send joins in DisposeAsync; disposal completion is the observable cleanup outcome.");
    }

    [Test]
    public async Task CloseAsync_Should_Bound_Admission_Without_Leaving_A_Waiter()
    {
        var time = new ManualTimeProvider();
        using var socket = Substitute.For<ITerminalWebSocket>();
        using var queue = new TerminalSendQueue(socket, time);
        await queue.WaitAsync(CancellationToken.None);
        var close = queue.CloseAsync();
        try
        {
            await time.WaitForArmedAsync(TerminalSocketBounds.GracefulCloseTimeout, HangGuard.Bound);
            time.Advance(TerminalSocketBounds.GracefulCloseTimeout - TimeSpan.FromMilliseconds(1));
            await Assert.That(close.IsCompleted).IsFalse();
            await socket.DidNotReceive().CloseOutputAsync(Arg.Any<CancellationToken>());
            time.Advance(TimeSpan.FromMilliseconds(1));
            await close.WaitAsync(HangGuard.Bound);
            await socket.DidNotReceive().CloseOutputAsync(Arg.Any<CancellationToken>());
        }
        finally
        {
            queue.Release();
            await close.WaitAsync(HangGuard.Bound);
        }

        await queue.WaitAsync(CancellationToken.None).WaitAsync(HangGuard.Bound);
        queue.Release();
    }

    [Test]
    public async Task CloseAsync_Should_Bound_Close_Output_And_Release_Admission()
    {
        using var socket = Substitute.For<ITerminalWebSocket>();
        using var cleanup = new CancellationTokenSource();
        using var queue = CreateGatedCloseQueue(socket, cleanup.Token, out var time, out var entered);
        var close = queue.CloseAsync();
        try
        {
            var token = await entered.WaitAsync(HangGuard.Bound);
            await time.WaitForArmedAsync(TerminalSocketBounds.GracefulCloseTimeout, HangGuard.Bound);
            time.Advance(TerminalSocketBounds.GracefulCloseTimeout - TimeSpan.FromMilliseconds(1));
            await Assert.That(token.IsCancellationRequested).IsFalse();
            await Assert.That(close.IsCompleted).IsFalse();
            time.Advance(TimeSpan.FromMilliseconds(1));
            await close.WaitAsync(HangGuard.Bound);
            await AssertAdmissionReleasedAsync(queue, token);
        }
        finally
        {
            await cleanup.CancelOnWorkerAsync();
            await close.WaitAsync(HangGuard.Bound);
        }
    }

    [Test]
    public async Task CloseAsync_Should_Give_Close_Output_A_Fresh_Budget_After_Admission()
    {
        using var socket = Substitute.For<ITerminalWebSocket>();
        using var cleanup = new CancellationTokenSource();
        using var queue = CreateGatedCloseQueue(socket, cleanup.Token, out var time, out var entered);
        await queue.WaitAsync(CancellationToken.None);
        var held = true;
        var close = queue.CloseAsync();
        try
        {
            await time.WaitForArmedAsync(TerminalSocketBounds.GracefulCloseTimeout, HangGuard.Bound);
            time.Advance(TerminalSocketBounds.GracefulCloseTimeout - TimeSpan.FromMilliseconds(1));
            queue.Release();
            held = false;
            var token = await entered.WaitAsync(HangGuard.Bound);
            await time.WaitForArmedAsync(TerminalSocketBounds.GracefulCloseTimeout, HangGuard.Bound);
            time.Advance(TimeSpan.FromMilliseconds(1));
            await Assert.That(token.IsCancellationRequested).IsFalse();
            await Assert.That(close.IsCompleted).IsFalse();
            time.Advance(TerminalSocketBounds.GracefulCloseTimeout - TimeSpan.FromMilliseconds(2));
            await Assert.That(token.IsCancellationRequested).IsFalse();
            time.Advance(TimeSpan.FromMilliseconds(1));
            await close.WaitAsync(HangGuard.Bound);
            await AssertAdmissionReleasedAsync(queue, token);
        }
        finally
        {
            if (held)
            {
                queue.Release();
            }

            await cleanup.CancelOnWorkerAsync();
            await close.WaitAsync(HangGuard.Bound);
        }
    }

    private static TerminalSendQueue CreateGatedCloseQueue(
        ITerminalWebSocket socket, CancellationToken cleanup, out ManualTimeProvider time, out Task<CancellationToken> closeEntered)
    {
        var entered = new TaskCompletionSource<CancellationToken>(TaskCreationOptions.RunContinuationsAsynchronously);
        _ = socket.CloseOutputAsync(Arg.Any<CancellationToken>()).Returns(async call =>
        {
            var token = call.Arg<CancellationToken>();
            var release = new TaskCompletionSource<bool>(TaskCreationOptions.RunContinuationsAsynchronously);
            using var cancellation = token.Register(() => release.TrySetCanceled(token));
            using var completion = cleanup.Register(() => release.TrySetResult(true));
            _ = entered.TrySetResult(token);
            await release.Task;
        });
        closeEntered = entered.Task;
        time = new ManualTimeProvider();
        return new TerminalSendQueue(socket, time);
    }

    private static async Task AssertAdmissionReleasedAsync(TerminalSendQueue queue, CancellationToken token)
    {
        await Assert.That(token.IsCancellationRequested).IsTrue();
        // The expired token is observed, not used to cancel the admission check.
        await queue.WaitAsync(CancellationToken.None).WaitAsync(HangGuard.Bound, CancellationToken.None);
        queue.Release();
    }
}
