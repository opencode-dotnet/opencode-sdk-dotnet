using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Classifies one query parameter that the pinned document projects as a plain string, and that
/// stays a string by design: free text, an identifier, a path, an opaque token. Every plain-string
/// query parameter carries either this row or a <see cref="QueryParameterTypeCuration"/> row, so a
/// refresh that adds one stops until someone decides which it is.
/// </summary>
internal sealed record StringQueryParameterCuration : IReasonedRow
{
    /// <summary>Gets the operation the query parameter belongs to.</summary>
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }

    /// <summary>Gets the query parameter's wire name.</summary>
    [JsonPropertyName("parameter")] public required string Parameter { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
