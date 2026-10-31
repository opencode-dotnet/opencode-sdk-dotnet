using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>
/// A machine-checkable repair predicate: <c>componentLacksKeyword</c> names components and a
/// keyword; <c>parameterLacksStyle</c> names operations, a parameter and a style. A target already
/// satisfying the repair retires the patch, and a missing target demands human review.
/// </summary>
internal sealed record PatchPredicate
{
    /// <summary>A repair over component schemas and a keyword.</summary>
    public const string ComponentLacksKeyword = "componentLacksKeyword";

    /// <summary>A repair over operation parameters and a style.</summary>
    public const string ParameterLacksStyle = "parameterLacksStyle";

    [JsonPropertyName("type")] public required string Type { get; init; }

    [JsonPropertyName("components")]
    public IReadOnlyList<string>? Components
    {
        get;
        init
        {
            field = value is null ? null : Array.AsReadOnly([.. value]);
        }
    }

    [JsonPropertyName("keyword")] public string? Keyword { get; init; }

    [JsonPropertyName("operations")]
    public IReadOnlyList<string>? Operations
    {
        get;
        init
        {
            field = value is null ? null : Array.AsReadOnly([.. value]);
        }
    }

    [JsonPropertyName("parameter")] public string? Parameter { get; init; }

    [JsonPropertyName("style")] public string? Style { get; init; }
}
