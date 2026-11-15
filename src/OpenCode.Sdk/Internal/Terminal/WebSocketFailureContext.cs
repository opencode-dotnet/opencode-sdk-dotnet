namespace OpenCode.Sdk.Internal;

/// <summary>Names one WebSocket product without rendering a credential-bearing URL.</summary>
internal sealed record WebSocketFailureContext(string Subject)
{
    public static WebSocketFailureContext Terminal { get; } = new("PTY");

    public string Prefix => $"The opencode {Subject} WebSocket";
}
