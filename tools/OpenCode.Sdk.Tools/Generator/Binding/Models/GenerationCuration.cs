using System.Collections.ObjectModel;
using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record GenerationCuration
{
    /// <summary>Plain text summaries and remarks keyed by generated C# names.</summary>
    [JsonPropertyName("descriptions")]
    public required IReadOnlyList<DescriptionCuration> Descriptions
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>The payload identifier projected explicitly by qualifying event records.</summary>
    [JsonPropertyName("sessionScopedEvent")]
    public SessionScopedEventCuration? SessionScopedEvent { get; init; }

    [JsonPropertyName("groups")]
    public required IReadOnlyDictionary<string, GroupCuration> Groups
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = new ReadOnlyDictionary<string, GroupCuration>(new Dictionary<string, GroupCuration>(value, StringComparer.Ordinal));
        }
    }

    [JsonPropertyName("operationIdentities")]
    public required IReadOnlyList<OperationIdentityCuration> OperationIdentities
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    [JsonPropertyName("operationNames")]
    public required IReadOnlyList<OperationNameCuration> OperationNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    [JsonPropertyName("parameterNames")]
    public required IReadOnlyList<ParameterNameCuration> ParameterNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    [JsonPropertyName("schemaNames")]
    public required IReadOnlyList<SchemaNameCuration> SchemaNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    [JsonPropertyName("envelopePayloadNames")]
    public required IReadOnlyList<EnvelopePayloadNameCuration> EnvelopePayloadNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    [JsonPropertyName("schemaAliases")]
    public required IReadOnlyList<SchemaAlias> SchemaAliases
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

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
    }

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
    }

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
    }

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
    }

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
    }

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
    }

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
    }

    /// <summary>Owners of the models no family prefix places, each with its reason.</summary>
    [JsonPropertyName("modelOwners")]
    public required IReadOnlyList<ModelOwnerCuration> ModelOwners
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>The areas the Sessions family's models split into, each with its reason.</summary>
    [JsonPropertyName("sessionAreas")]
    public required IReadOnlyList<SessionAreaCuration> SessionAreas
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>The query parameters the document projects as strings that bind as integers, each with its decode evidence.</summary>
    [JsonPropertyName("queryParameterTypes")]
    public required IReadOnlyList<QueryParameterTypeCuration> QueryParameterTypes
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>The plain-string query parameters that stay strings by design, each with its reason.</summary>
    [JsonPropertyName("stringQueryParameters")]
    public required IReadOnlyList<StringQueryParameterCuration> StringQueryParameters
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }
}
