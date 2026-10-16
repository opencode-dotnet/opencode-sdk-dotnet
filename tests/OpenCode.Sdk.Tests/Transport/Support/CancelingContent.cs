using System.Net;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Transport.Support;

/// <summary>Cancels the caller token while a one-shot response body is being buffered.</summary>
internal sealed class CancelingContent : HttpContent
{
    private readonly CancellationTokenSource _cancellation;

    public CancelingContent(CancellationTokenSource cancellation)
    {
        ArgumentNullException.ThrowIfNull(cancellation);
        _cancellation = cancellation;
    }

    public bool IsDisposed { get; private set; }

    protected override async Task SerializeToStreamAsync(Stream stream, TransportContext? context)
    {
        await _cancellation.CancelOnWorkerAsync();
        throw new OperationCanceledException(_cancellation.Token);
    }

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
