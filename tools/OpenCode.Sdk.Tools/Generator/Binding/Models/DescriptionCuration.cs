using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>Plain text documentation for one generated C# type or property.</summary>
internal sealed record DescriptionCuration : IReasonedRow
{
    [JsonPropertyName("type")]
    public required string Type { get; init; }

    [JsonPropertyName("member")]
    public string? Member { get; init; }

    [JsonPropertyName("summary")]
    public string? Summary { get; init; }

    [JsonPropertyName("remarks")]
    public string? Remarks { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }

    [JsonPropertyName("sources")]
    public IReadOnlyList<DescriptionSource?>? Sources { get; init; }
}
