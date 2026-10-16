using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Names the interface a hoisted promoted-object member is declared with. Curation chooses the
/// .NET name of a construct the pinned document already represents; it adds no wire semantics
/// (ADR-0013).
/// </summary>
internal sealed record HoistedMemberNameCuration : IReasonedRow
{
    /// <summary>Gets the interface that declares the member: a union interface or a hoisted carrier.</summary>
    [JsonPropertyName("owner")] public required string Owner { get; init; }

    /// <summary>Gets the member's wire property name.</summary>
    [JsonPropertyName("property")] public required string Property { get; init; }

    [JsonPropertyName("dotnetName")] public required string DotNetName { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
