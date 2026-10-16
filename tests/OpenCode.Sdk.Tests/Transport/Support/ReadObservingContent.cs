using System.Net;

namespace OpenCode.Sdk.Tests.Transport.Support;

/// <summary>A body that completes immediately and records whether the pipeline drained it.</summary>
internal sealed class ReadObservingContent : HttpContent
{
    private readonly byte[] _payload;

    public ReadObservingContent(byte[] payload)
    {
        ArgumentNullException.ThrowIfNull(payload);

        _payload = payload;
    }

    public bool WasRead { get; private set; }

    public bool IsDisposed { get; private set; }

    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        WriteAsync(stream);

#if NET
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context,
        CancellationToken cancellationToken) =>
        WriteAsync(stream);
#endif

    protected override bool TryComputeLength(out long length)
    {
        length = _payload.Length;
        return true;
    }

    protected override void Dispose(bool disposing)
    {
        if (disposing)
        {
            IsDisposed = true;
        }

        base.Dispose(disposing);
    }

    private Task WriteAsync(Stream stream)
    {
        WasRead = true;
        return stream.WriteAsync(_payload, 0, _payload.Length);
    }
}
