using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>A quoted upstream declaration supporting documentation, not protocol semantics.</summary>
internal sealed record DescriptionSource
{
    [JsonPropertyName("location")]
    public string? Location { get; init; }

    [JsonPropertyName("excerpt")]
    public string? Excerpt { get; init; }
}
