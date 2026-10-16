using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record OperationNameCuration : IReasonedRow
{
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }

    [JsonPropertyName("methodName")] public required string MethodName { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
