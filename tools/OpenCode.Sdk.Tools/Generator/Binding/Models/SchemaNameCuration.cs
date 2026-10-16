using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record SchemaNameCuration : IReasonedRow
{
    [JsonPropertyName("schema")] public required string Schema { get; init; }

    [JsonPropertyName("dotnetName")] public required string DotNetName { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
