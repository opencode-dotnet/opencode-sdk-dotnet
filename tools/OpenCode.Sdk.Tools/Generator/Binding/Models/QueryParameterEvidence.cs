using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Locates the upstream decode schema behind a <see cref="QueryParameterTypeCuration"/> row: a
/// file the source watch pins, and the text in it that declares the decode. A refresh that
/// changes the file stops at the watch, so the row is reviewed with the schema it rests on.
/// </summary>
internal sealed record QueryParameterEvidence
{
    /// <summary>Gets the submodule-relative path of the upstream file; the source watch must name it.</summary>
    [JsonPropertyName("path")] public required string Path { get; init; }

    /// <summary>Gets the text in that file that declares the parameter's decode.</summary>
    [JsonPropertyName("anchor")] public required string Anchor { get; init; }
}
