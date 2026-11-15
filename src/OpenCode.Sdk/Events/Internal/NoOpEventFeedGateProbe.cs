namespace OpenCode.Sdk.Internal;

/// <summary>The production gate-entry probe; publication proceeds without interception.</summary>
internal sealed class NoOpEventFeedGateProbe : IEventFeedGateProbe
{
    public void OnPublication()
    {
        // Production has no publication barrier beyond the feed gate itself.
    }
}
