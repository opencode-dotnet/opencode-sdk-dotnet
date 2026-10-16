using System.Net;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Holds a response body open until the read token is cancelled.</summary>
internal sealed class BlockingContent : HttpContent
{
    private readonly Gate _gate = new();
    private bool _disposed;

    public BlockingContent()
    {
        ReadCompleted = _gate.Completed;
        ReadStarted = _gate.Started;
    }

    public bool IsDisposed => _disposed;

    public Task ReadCompleted { get; }

    public Task ReadStarted { get; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        _gate.WaitAsync(CancellationToken.None);

#if NET
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context,
        CancellationToken cancellationToken) =>
        _gate.WaitAsync(cancellationToken);
#endif

    protected override void Dispose(bool disposing)
    {
        if (disposing && !_disposed)
        {
            _disposed = true;
            _gate.Dispose();
        }

        base.Dispose(disposing);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
