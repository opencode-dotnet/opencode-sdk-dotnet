using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Types one query parameter that the pinned document projects as a plain string while
/// upstream's server decodes it as a number and upstream's own client takes a number. The row
/// names the C# integer the parameter binds to, why, and the upstream decode schema that proves
/// it; the wire still carries the value as query text (ADR-0037).
/// </summary>
internal sealed record QueryParameterTypeCuration : IReasonedRow
{
    /// <summary>Gets the operation the query parameter belongs to.</summary>
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }

    /// <summary>Gets the query parameter's wire name.</summary>
    [JsonPropertyName("parameter")] public required string Parameter { get; init; }

    /// <summary>Gets the C# integer keyword the parameter binds to: <c>int</c> or <c>long</c>.</summary>
    [JsonPropertyName("dotnetType")] public required string DotNetType { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }

    /// <summary>Gets the upstream decode schema that types the parameter.</summary>
    [JsonPropertyName("evidence")] public required QueryParameterEvidence Evidence { get; init; }
}
