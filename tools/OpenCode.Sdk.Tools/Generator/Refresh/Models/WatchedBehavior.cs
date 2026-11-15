using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>An upstream behavior this SDK or opencode-agents-ai depends on, with its owner and content anchor.</summary>
internal sealed record WatchedBehavior
{
    [JsonPropertyName("owner")] public required string Owner { get; init; }

    [JsonPropertyName("behavior")] public required string Behavior { get; init; }

    [JsonPropertyName("anchor")] public required SourceAnchor Anchor { get; init; }
}
