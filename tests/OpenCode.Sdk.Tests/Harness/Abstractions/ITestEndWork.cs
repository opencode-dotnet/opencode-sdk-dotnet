namespace OpenCode.Sdk.Tests.Harness.Abstractions;

internal interface ITestEndWork
{
    public Task CheckNoInvocationLeftPendingAsync();

    public Task EvictFinishedLocationsAsync(CancellationToken cancellationToken);

    public void ReportEvictionFailure(Exception exception);
}
