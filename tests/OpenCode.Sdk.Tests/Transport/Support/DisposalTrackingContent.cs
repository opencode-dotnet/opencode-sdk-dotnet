namespace OpenCode.Sdk.Tests.Transport.Support;

/// <summary>Tracks whether the response owner disposed a buffered string body.</summary>
internal sealed class DisposalTrackingContent : StringContent
{
    public DisposalTrackingContent(string content)
        : base(content)
    {
    }

    public bool IsDisposed { get; private set; }

    protected override void Dispose(bool disposing)
    {
        IsDisposed = true;
        base.Dispose(disposing);
    }
}
