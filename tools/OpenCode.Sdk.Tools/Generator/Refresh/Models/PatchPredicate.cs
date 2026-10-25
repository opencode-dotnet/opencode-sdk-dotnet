using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh.Models;

/// <summary>
/// A machine-checkable repair predicate. The single supported kind,
/// <c>componentLacksKeyword</c>, holds while every named component schema exists and lacks the
/// keyword; a component already carrying it retires the patch, and a missing component demands
/// human review.
/// </summary>
internal sealed record PatchPredicate
{
    /// <summary>The only supported predicate kind today.</summary>
    public const string ComponentLacksKeyword = "componentLacksKeyword";

    [JsonPropertyName("type")] public required string Type { get; init; }

    [JsonPropertyName("components")]
    public required IReadOnlyList<string> Components
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    [JsonPropertyName("keyword")] public required string Keyword { get; init; }
}
