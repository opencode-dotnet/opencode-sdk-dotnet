using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Names the C# member one enum value of a pinned schema becomes, where the mechanical Pascal
/// casing would produce a name the reviewed surface does not want (a digit-leading value becomes
/// <c>Value…</c>). Curation chooses the .NET name of a construct the pinned document already
/// represents; it adds no wire semantics (ADR-0013).
/// </summary>
internal sealed record EnumMemberNameCuration : IReasonedRow
{
    /// <summary>Gets the schema key of the enum, as the document names it.</summary>
    [JsonPropertyName("schema")] public required string Schema { get; init; }

    /// <summary>Gets the wire value the member represents, exactly as the document spells it.</summary>
    [JsonPropertyName("value")] public required string Value { get; init; }

    [JsonPropertyName("dotnetName")] public required string DotNetName { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
