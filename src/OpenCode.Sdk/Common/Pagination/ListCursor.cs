using System.Text.Json.Serialization;

namespace OpenCode.Sdk;

/// <summary>The wire cursor of a list page; absent directions mean no further page.</summary>
public sealed record ListCursor
{
    /// <summary>Gets the opaque cursor of the previous page, when one exists.</summary>
    [JsonPropertyName("previous")]
    public string? Previous { get; init; }

    /// <summary>Gets the opaque cursor of the next page, when one exists.</summary>
    [JsonPropertyName("next")]
    public string? Next { get; init; }
}
