using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Hosting.Launcher.Support;

/// <summary>Exercises pid arrival and named timeout diagnostics without starting a process.</summary>
public sealed class LadderTreeTests
{
    /// <summary>Bounds a wait that never ends; it decides nothing else.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Test]
    [Timeout(60_000)]
    public async Task WaitForPidAsync_Should_Name_The_Role_Bound_And_Stderr_When_The_Guard_Fires(CancellationToken cancellationToken)
    {
        var time = new ManualTimeProvider();
        var output = new OpenCodeServerOutput();
        var bound = TimeSpan.FromSeconds(1.5);
        output.AppendStandardError("ladder-tree role=root pid=1234");
        var wait = LadderTree.WaitForPidAsync(output, LadderTree.GroupChild, bound, time);
        output.AppendStandardError("the child has not reported");

        await time.WaitForArmedAsync(bound, HangGuard).WaitAsync(cancellationToken);
        await Assert.That(wait.IsCompleted).IsFalse();
        time.Advance(bound);

        var failure = await Assert.That(wait).Throws<TimeoutException>();
        await Assert.That(failure!.Message)
            .IsEqualTo("The ladder tree reported no 'group-child' pid within 1.5 s: ladder-tree role=root pid=1234 | the child has not reported");
        var cancellation = await Assert.That(failure.InnerException).IsAssignableTo<OperationCanceledException>();
        // The assignability assertion guarantees an inner cancellation exception.
        await Assert.That(cancellation!.CancellationToken.IsCancellationRequested).IsTrue();
    }

    [Test]
    [Timeout(60_000)]
    public async Task WaitForPidAsync_Should_Return_The_Pid_That_Arrives_During_The_Wait(CancellationToken cancellationToken)
    {
        var time = new ManualTimeProvider();
        var output = new OpenCodeServerOutput();
        var pause = TimeSpan.FromMilliseconds(10);
        var wait = LadderTree.WaitForPidAsync(output, LadderTree.GroupChild, HangGuard, time);

        await time.WaitForArmedAsync(pause, HangGuard).WaitAsync(cancellationToken);
        output.AppendStandardError("ladder-tree role=group-child pid=4321");
        time.Advance(pause);

        await Assert.That(await wait).IsEqualTo(4321);
    }
}
