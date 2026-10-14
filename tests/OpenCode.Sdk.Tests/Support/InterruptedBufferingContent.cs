using System.Net;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// Reproduces .NET 10's <c>HttpContent</c> failure mode deterministically: the read-stream
/// materialization blocks until the content is disposed, then fails with the arbitrary
/// exception a torn-down pooled buffer yields (<see cref="ArgumentNullException"/> for the
/// stream's array) instead of <see cref="ObjectDisposedException"/>. Downlevel, the polyfilled
/// token overload abandons the read once the token cancels, so the fault there is an unobserved
/// task nobody awaits — the real BCL never faults on that path.
/// </summary>
internal sealed class InterruptedBufferingContent : HttpContent
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
        throw new InvalidOperationException("The read-stream path owns this content; it is never serialized.");

    protected override Task<Stream> CreateContentReadStreamAsync() => MaterializeAsync();

#if NET
    protected override Task<Stream> CreateContentReadStreamAsync(CancellationToken cancellationToken) =>
        MaterializeAsync();
#endif

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

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

    private async Task<Stream> MaterializeAsync()
    {
        _ = _readStarted.TrySetResult();
        try
        {
            await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);
        }
        finally
        {
            _ = _readCompleted.TrySetResult();
        }

        // Disposal returned the pooled buffer; the BCL builds the stream over a null array and
        // throws exactly what the runtime throws for it.
        return new MemoryStream(ReturnedBuffer()!, 0, 0, writable: false);
    }

    /// <summary>The pooled array after <c>ReturnAllPooledBuffers</c>: gone.</summary>
    private static byte[]? ReturnedBuffer() => null;
}
