using OpenCode.Sdk.Tests.Harness.Ownership;

namespace OpenCode.Sdk.Tests.Harness;

public sealed class GitProcessCancellationTests
{
    [Test]
    public async Task StopAsync_Should_Attempt_Exit_Observation_And_Preserve_Every_Failure_When_Interrupt_Fails()
    {
        var primary = new OperationCanceledException("Git command canceled.");
        var interruptFailure = new InvalidOperationException("Git process interruption failed.");
        var waitFailure = new IOException("Git process exit observation failed.");
        var waitAttempted = false;
        var cancellation = new GitProcessCancellation(TimeSpan.FromSeconds(5));

        var caught = await Assert.That(async () => await cancellation.StopAsync(
            () => false,
            () => throw interruptFailure,
            _ =>
            {
                waitAttempted = true;
                return Task.FromException(waitFailure);
            },
            primary)).Throws<OperationCanceledException>();

        await Assert.That(ReferenceEquals(caught, primary)).IsTrue();
        await Assert.That(waitAttempted).IsTrue();
        await Assert.That(caught!.Data[OwnedCleanup.FailuresKey]).IsTypeOf<AggregateException>();
        var failures = caught.Data[OwnedCleanup.FailuresKey] as AggregateException;
        await Assert.That(failures!.InnerExceptions.Count).IsEqualTo(2);
        await Assert.That(ReferenceEquals(failures.InnerExceptions[0], interruptFailure)).IsTrue();
        await Assert.That(ReferenceEquals(failures.InnerExceptions[1], waitFailure)).IsTrue();
    }
}
