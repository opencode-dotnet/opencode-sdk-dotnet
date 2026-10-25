using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// The rule that marks session-scoped events: which event unions to look in, the payload member and
/// the identifier member it must carry, and the identifier schema that member must be.
/// </summary>
internal sealed record SessionScopedEventCuration : IReasonedRow
{
    /// <summary>Gets the generated event unions whose records are judged.</summary>
    [JsonPropertyName("unions")]
    public required IReadOnlyList<string> Unions
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>Gets the wire name of the record's payload member.</summary>
    [JsonPropertyName("payloadMember")]
    public required string PayloadMember { get; init; }

    /// <summary>Gets the wire name of the payload's identifier member.</summary>
    [JsonPropertyName("member")]
    public required string Member { get; init; }

    /// <summary>Gets the schema the identifier member must reference.</summary>
    [JsonPropertyName("identifier")]
    public required string Identifier { get; init; }

    /// <summary>Gets why the rule exists.</summary>
    [JsonPropertyName("reason")]
    public required string Reason { get; init; }
}
