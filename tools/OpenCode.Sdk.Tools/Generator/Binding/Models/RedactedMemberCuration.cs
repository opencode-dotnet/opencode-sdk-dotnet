using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// A judgement on one model member's printed value, beside the upstream field list the generator
/// masks mechanically (ADR-0028): <c>redact: true</c> masks a member the list does not name,
/// <c>redact: false</c> prints one the list names. The reason states what the value is.
/// </summary>
internal sealed record RedactedMemberCuration : IReasonedRow
{
    /// <summary>Gets the generated model's C# type name.</summary>
    [JsonPropertyName("model")] public required string Model { get; init; }

    /// <summary>Gets the member's wire name.</summary>
    [JsonPropertyName("property")] public required string Property { get; init; }

    [JsonPropertyName("redact")] public required bool Redact { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
