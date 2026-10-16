using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record EnvelopePayloadNameCuration : IReasonedRow
{
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }

    [JsonPropertyName("name")] public required string Name { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
