using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Declares one component schema as a spelling of another; the collapse is validated
/// fail-closed for structural identity so upstream drift on either side breaks generation.
/// </summary>
internal sealed record SchemaAlias : IReasonedRow
{
    [JsonPropertyName("schema")]
    public required string Schema { get; init; }

    [JsonPropertyName("aliasOf")]
    public required string AliasOf { get; init; }

    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
