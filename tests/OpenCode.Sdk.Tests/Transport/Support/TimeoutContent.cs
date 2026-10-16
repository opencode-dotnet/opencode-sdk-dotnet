using System.Net;

namespace OpenCode.Sdk.Tests.Transport.Support;

/// <summary>Simulates the transport timing out while the response body streams.</summary>
internal sealed class TimeoutContent : HttpContent
{
    protected override Task SerializeToStreamAsync(Stream stream, TransportContext? context) =>
        throw new TaskCanceledException();

    protected override bool TryComputeLength(out long length)
    {
        length = 0;
        return false;
    }
}
