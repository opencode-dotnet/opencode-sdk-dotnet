using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Names the C# parameter one route value of an operation becomes, where the mechanical camel
/// casing of the wire name says less than the rest of the surface: a bare <c>id</c> beside the
/// other families' <c>sessionId</c> and <c>ptyId</c>. The wire name still fills the route.
/// Curation chooses the .NET name of a construct the pinned document already represents; it adds
/// no wire semantics.
/// </summary>
internal sealed record ParameterNameCuration : IReasonedRow
{
    /// <summary>Gets the operation the route value belongs to.</summary>
    [JsonPropertyName("operationId")] public required string OperationId { get; init; }

    /// <summary>Gets the route value's wire name, exactly as the path template spells it.</summary>
    [JsonPropertyName("parameter")] public required string Parameter { get; init; }

    [JsonPropertyName("dotnetName")] public required string DotNetName { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
