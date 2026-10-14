using System.Net;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Holds a response body open until the read token is cancelled.</summary>
internal sealed class BlockingContent : HttpContent
{
    private readonly SemaphoreSlim _gate = new(initialCount: 0, maxCount: 1);
    private readonly TaskCompletionSource _readCompleted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private readonly TaskCompletionSource _readStarted = new(TaskCreationOptions.RunContinuationsAsynchronously);
    private bool _disposed;

    public bool IsDisposed => _disposed;

#pragma warning disable VSTHRD003 // Test signals complete on their own source and resume asynchronously; exposing the task is the signal's contract.
    public Task ReadCompleted => _readCompleted.Task;

    public Task ReadStarted => _readStarted.Task;
#pragma warning restore VSTHRD003

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        WaitAsync(CancellationToken.None);

#if NET
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context,
        CancellationToken cancellationToken) =>
        WaitAsync(cancellationToken);
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _gate.Release();
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task WaitAsync(CancellationToken cancellationToken)
    {
        _ = _readStarted.TrySetResult();
        try
        {
            await _gate.WaitAsync(cancellationToken).ConfigureAwait(false);
        }
        finally
        {
            _ = _readCompleted.TrySetResult();
        }
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
