using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Clears one wire name the secret-name wall stops (ADR-0028): the name contains a word upstream
/// treats as a secret marker, but the value it carries is not a credential wherever it appears.
/// The reason states what the value is.
/// </summary>
internal sealed record SecretLookingNameCuration : IReasonedRow
{
    /// <summary>Gets the wire name, as every model member that carries it is spelled.</summary>
    [JsonPropertyName("property")] public required string Property { get; init; }

    [JsonPropertyName("reason")] public required string Reason { get; init; }
}
