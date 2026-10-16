namespace OpenCode.Sdk.Tests.Support;

/// <summary>Signals a wait's start and completion, releasing a pending wait when disposed.</summary>
internal sealed class Gate : IDisposable
{
    private readonly SemaphoreSlim _gate = new(initialCount: 0, maxCount: 1);
    private readonly TaskCompletionSource _completed = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _started = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

#pragma warning disable VSTHRD003 // Test signals complete on their own source and resume asynchronously; exposing the task is the signal's contract.
    public Task Completed => _completed.Task;

    public Task Started => _started.Task;
#pragma warning restore VSTHRD003

    public async Task WaitAsync(CancellationToken cancellationToken)
    {
        _ = _started.TrySetResult();
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _completed.TrySetResult();
        }
    }

    public void Dispose()
    {
        if (!_disposed)
        {
            _disposed = true;
            _gate.Release();
            _gate.Dispose();
        }
    }
}
