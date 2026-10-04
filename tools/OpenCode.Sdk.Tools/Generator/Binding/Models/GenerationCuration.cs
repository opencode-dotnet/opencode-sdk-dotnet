using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record GenerationCuration
{
    [JsonPropertyName("groups")]
    public required IReadOnlyDictionary<string, GroupCuration> Groups
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = new ReadOnlyDictionary<string, GroupCuration>(new Dictionary<string, GroupCuration>(value, StringComparer.Ordinal));
        }
    } = new ReadOnlyDictionary<string, GroupCuration>(new Dictionary<string, GroupCuration>(StringComparer.Ordinal));

    [JsonPropertyName("operationIdentities")]
    public required IReadOnlyList<OperationIdentityCuration> OperationIdentities
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<OperationIdentityCuration>());

    [JsonPropertyName("operationNames")]
    public required IReadOnlyList<OperationNameCuration> OperationNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<OperationNameCuration>());

    [JsonPropertyName("schemaNames")]
    public required IReadOnlyList<SchemaNameCuration> SchemaNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<SchemaNameCuration>());

    [JsonPropertyName("envelopePayloadNames")]
    public required IReadOnlyDictionary<string, string> EnvelopePayloadNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(value, StringComparer.Ordinal));
        }
    } = new ReadOnlyDictionary<string, string>(new Dictionary<string, string>(StringComparer.Ordinal));

    [JsonPropertyName("schemaAliases")]
    public required IReadOnlyList<SchemaAlias> SchemaAliases
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<SchemaAlias>());

    /// <summary>Fingerprints for operations that are never selected but that hand-written code depends on (ADR-0021).</summary>
    [JsonPropertyName("transportOwned")]
    public required IReadOnlyList<TransportOwnedCuration> TransportOwned
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<TransportOwnedCuration>());

    /// <summary>Names for the interfaces hoisted promoted-object members are declared with.</summary>
    [JsonPropertyName("hoistedMemberNames")]
    public required IReadOnlyList<HoistedMemberNameCuration> HoistedMemberNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<HoistedMemberNameCuration>());

    /// <summary>Names for enum members whose mechanical Pascal casing the reviewed surface does not want.</summary>
    [JsonPropertyName("enumMemberNames")]
    public required IReadOnlyList<EnumMemberNameCuration> EnumMemberNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<EnumMemberNameCuration>());

    /// <summary>Judgements on printed member values beside the upstream redaction list (ADR-0028).</summary>
    [JsonPropertyName("redactedMembers")]
    public required IReadOnlyList<RedactedMemberCuration> RedactedMembers
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<RedactedMemberCuration>());

    /// <summary>Judgements on every open model's printed extension data, one row per open model.</summary>
    [JsonPropertyName("redactedOpenMembers")]
    public required IReadOnlyList<RedactedOpenMembersCuration> RedactedOpenMembers
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<RedactedOpenMembersCuration>());

    /// <summary>Wire names the secret-name wall stops that carry no credential (ADR-0028).</summary>
    [JsonPropertyName("secretLookingNames")]
    public required IReadOnlyList<SecretLookingNameCuration> SecretLookingNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<SecretLookingNameCuration>());

    /// <summary>Operations a standing wall refuses and the maintainer has decided to leave out of the generated surface.</summary>
    [JsonPropertyName("declined")]
    public required IReadOnlyList<DeclinedCuration> Declined
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<DeclinedCuration>());
}
