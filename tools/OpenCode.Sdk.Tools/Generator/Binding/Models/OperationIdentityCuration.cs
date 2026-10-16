using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Maps an operation whose upstream identity violates upstream's own conventions onto its
/// intended identity (ADR-0013). The reason carries the upstream report; the row retires when
/// upstream's fix makes its subject vanish from the document.
/// </summary>
internal sealed record OperationIdentityCuration : IReasonedRow
{
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }

    [JsonPropertyName("identity")] public required string Identity { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
