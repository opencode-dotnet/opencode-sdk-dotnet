using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// The rule that marks session-scoped events: which event unions to look in, the payload member and
/// the identifier member it must carry, the identifier schema that member must be, and the
/// hand-written interface with its member the qualifying records implement.
/// </summary>
internal sealed record SessionScopedEventCuration
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
    } = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>Gets the hand-written interface a qualifying record implements.</summary>
    [JsonPropertyName("interface")]
    public required string Interface { get; init; }

    /// <summary>Gets the interface member the record implements explicitly.</summary>
    [JsonPropertyName("interfaceMember")]
    public required string InterfaceMember { get; init; }

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
