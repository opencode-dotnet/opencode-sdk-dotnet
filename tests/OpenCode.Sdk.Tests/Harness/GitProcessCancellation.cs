using OpenCode.Sdk.Tests.Harness.Ownership;
using OpenCode.Sdk.Tests.Harness.Ownership.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

internal sealed class GitProcessCancellation(TimeSpan timeout, IOwnedOperationDeadline deadline)
{
    public GitProcessCancellation(TimeSpan timeout) : this(timeout, new OwnedOperationDeadline())
    {
    }

    public async Task StopAsync(
        Func<bool> hasExited,
        Action interrupt,
        Func<CancellationToken, Task> waitForExit,
        OperationCanceledException primary)
    {
        ArgumentNullException.ThrowIfNull(hasExited);
        ArgumentNullException.ThrowIfNull(interrupt);
        ArgumentNullException.ThrowIfNull(waitForExit);
        ArgumentNullException.ThrowIfNull(primary);

        var cleanup = new OwnedCleanup(timeout, deadline);
        cleanup.Own("Git process interruption", _ =>
        {
            if (!hasExited())
            {
                interrupt();
            }

            return Task.CompletedTask;
        });
        cleanup.Own("Git process exit observation", waitForExit);
        await cleanup.CompleteAsync(primary);
    }
}
