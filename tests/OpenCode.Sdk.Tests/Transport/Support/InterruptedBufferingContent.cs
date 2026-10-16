using System.Net;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Transport.Support;

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
    private readonly Gate _gate = new();
    private bool _disposed;

    public InterruptedBufferingContent()
    {
        ReadCompleted = _gate.Completed;
        ReadStarted = _gate.Started;
    }

    public bool IsDisposed => _disposed;

    public Task ReadCompleted { get; }

    public Task ReadStarted { get; }

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
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }

    private async Task<Stream> MaterializeAsync()
    {
        await _gate.WaitAsync(CancellationToken.None).ConfigureAwait(false);

        // Disposal returned the pooled buffer; the BCL builds the stream over a null array and
        // throws exactly what the runtime throws for it.
        return new MemoryStream(ReturnedBuffer()!, 0, 0, writable: false);
    }

    /// <summary>The pooled array after <c>ReturnAllPooledBuffers</c>: gone.</summary>
    private static byte[]? ReturnedBuffer() => null;
}
