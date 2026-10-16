namespace OpenCode.Sdk.Tests.Harness.Ownership.Abstractions;

internal interface IOwnedOperationDeadline
{
    public Task WaitAsync(string name, Task operation, TimeSpan timeout, CancellationToken cancellationToken = default);
}
