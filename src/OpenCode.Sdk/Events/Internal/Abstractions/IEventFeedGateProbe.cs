namespace OpenCode.Sdk.Internal;

/// <summary>Test seam: lets a test order cancellation before publication selection; a no-op in production.</summary>
internal interface IEventFeedGateProbe
{
    public void OnPublication();
}
