namespace OpenCode.Sdk.Tests.Harness.Ownership;

internal sealed class OwnedCleanupOperation
{
    private readonly Func<CancellationToken, Task>? _operation;
    private readonly Task? _pending;

    public OwnedCleanupOperation(string name, Func<CancellationToken, Task> operation, TimeSpan? timeout = null)
    {
        Name = name;
        _operation = operation;
        Timeout = timeout;
    }

    public OwnedCleanupOperation(string name, Task pending, Func<OperationCanceledException, bool>? expectedCancellation)
    {
        Name = name;
        _pending = pending;
        ExpectedCancellation = expectedCancellation;
    }

    public string Name { get; }

    /// <summary>The operation's own budget, or <see langword="null"/> for the cleanup's shared one.</summary>
    public TimeSpan? Timeout { get; }

    public Func<OperationCanceledException, bool>? ExpectedCancellation { get; }

    public Task StartAsync(CancellationToken cancellationToken) =>
        _pending ?? Task.Run(() => _operation!(cancellationToken), CancellationToken.None);
}
