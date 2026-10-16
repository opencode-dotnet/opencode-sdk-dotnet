using System.Net;

namespace OpenCode.Sdk.Tests.Transport.Support;

/// <summary>Tracks whether the pipeline released the response as well as its content.</summary>
internal sealed class DisposalTrackingResponse : HttpResponseMessage
{
    public DisposalTrackingResponse(HttpStatusCode statusCode, HttpContent content)
        : base(statusCode)
    {
        ArgumentNullException.ThrowIfNull(content);
        Content = content;
    }

    public bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
