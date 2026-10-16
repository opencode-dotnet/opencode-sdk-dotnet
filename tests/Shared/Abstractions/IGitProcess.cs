namespace OpenCode.Sdk.Tests.Harness.Abstractions;

internal interface IGitProcess
{
    public Task RunAsync(string workingDirectory, string arguments, CancellationToken cancellationToken);
}
