using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>One behavior's content anchor and its verdict at the prepared commit.</summary>
internal sealed record ReceiptWatchedAnchor
{
    [JsonPropertyName("text")] public required string Text { get; init; }

    [JsonPropertyName("matched")] public required bool Matched { get; init; }
}
