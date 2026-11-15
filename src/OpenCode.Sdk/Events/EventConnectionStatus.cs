namespace OpenCode.Sdk;

/// <summary>The shared event source's current connection phase, not a subscription position or liveness proof.</summary>
public enum EventConnectionStatus
{
    /// <summary>No admitted event subscription owns a source.</summary>
    Idle,
    /// <summary>The first source attempt has not delivered its connection marker.</summary>
    Connecting,
    /// <summary>The source received a validated connection marker.</summary>
    Connected,
    /// <summary>An attempt failed and the source is retrying.</summary>
    Reconnecting,
}
