using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Tests.Support;

internal static class BindingScenarioData
{
    public static OperationSelection Selection(params string[] operationIds) =>
        new()
        {
            OperationIds = operationIds,
        };

    public static GenerationCuration Curation(IReadOnlyDictionary<string, GroupCuration> groups,
        IReadOnlyDictionary<string, string>? envelopePayloadNames = null,
        IReadOnlyList<SchemaAlias>? schemaAliases = null,
        IReadOnlyList<OperationNameCuration>? operationNames = null,
        IReadOnlyList<SchemaNameCuration>? schemaNames = null,
        IReadOnlyList<OperationIdentityCuration>? operationIdentities = null,
        IReadOnlyList<TransportOwnedCuration>? transportOwned = null,
        IReadOnlyList<DeclinedCuration>? declined = null,
        IReadOnlyList<HoistedMemberNameCuration>? hoistedMemberNames = null,
        IReadOnlyList<EnumMemberNameCuration>? enumMemberNames = null,
        IReadOnlyList<RedactedMemberCuration>? redactedMembers = null,
        IReadOnlyList<SecretLookingNameCuration>? secretLookingNames = null,
        IReadOnlyList<RedactedOpenMembersCuration>? redactedOpenMembers = null) =>
        new()
        {
            Groups = groups,
            OperationIdentities = operationIdentities ?? [],
            OperationNames = operationNames ?? [],
            SchemaNames = schemaNames ?? [],
            EnvelopePayloadNames = envelopePayloadNames ?? new Dictionary<string, string>(StringComparer.Ordinal),
            SchemaAliases = schemaAliases ?? [],
            TransportOwned = transportOwned ?? [],
            Declined = declined ?? [],
            HoistedMemberNames = hoistedMemberNames ?? [],
            EnumMemberNames = enumMemberNames ?? [],
            RedactedMembers = redactedMembers ?? [],
            RedactedOpenMembers = redactedOpenMembers ?? [],
            SecretLookingNames = secretLookingNames ?? [],
        };

    public static DeclinedCuration Declined(string operationId,
        string reason = "A standing wall refuses the operation and the maintainer decided the wall stands.") =>
        new()
        {
            OperationId = operationId,
            Reason = reason,
        };

    public static TransportOwnedCuration TransportOwned(string operationId, string subtreeSha256,
        string reason = "The operation is transport-owned; a hand-written door depends on its shape (ADR-0021).") =>
        new()
        {
            OperationId = operationId,
            SubtreeSha256 = subtreeSha256,
            Reason = reason,
        };

    public static RedactedOpenMembersCuration OpenMembers(string model, bool redact,
        string reason = "The scenario decides its open model's printed extension data explicitly.") =>
        new()
        {
            Model = model,
            Redact = redact,
            Reason = reason,
        };

    public static OperationIdentityCuration OperationIdentity(string operationId, string identity,
        string reason = "Upstream leaks the Effect group qualification into the operationId (reported upstream).") =>
        new()
        {
            OperationId = operationId,
            Identity = identity,
            Reason = reason,
        };

    public static SchemaAlias Alias(string schema, string aliasOf, string reason = "The upstream spec emits a duplicate component.") =>
        new()
        {
            Schema = schema,
            AliasOf = aliasOf,
            Reason = reason,
        };

    public static OperationNameCuration OperationName(string operationId, string methodName,
        string reason = "The reviewed .NET surface requires an explicit operation name.") =>
        new()
        {
            OperationId = operationId,
            MethodName = methodName,
            Reason = reason,
        };

    public static SchemaNameCuration SchemaName(string schema, string dotnetName,
        string reason = "The reviewed .NET surface requires an explicit schema name.") =>
        new()
        {
            Schema = schema,
            DotNetName = dotnetName,
            Reason = reason,
        };

    public static HoistedMemberNameCuration HoistedMemberName(string owner, string property, string dotnetName,
        string reason = "The reviewed .NET surface names the hoisted member's carrier explicitly.") =>
        new()
        {
            Owner = owner,
            Property = property,
            DotNetName = dotnetName,
            Reason = reason,
        };

    public static EnumMemberNameCuration EnumMemberName(string schema, string value, string dotnetName,
        string reason = "The reviewed .NET surface names the enum member explicitly.") =>
        new()
        {
            Schema = schema,
            Value = value,
            DotNetName = dotnetName,
            Reason = reason,
        };

    public static Dictionary<string, GroupCuration> Groups() => new(StringComparer.Ordinal);

    public static Dictionary<string, GroupCuration> Groups(string wireName, GroupCuration group) =>
        new(StringComparer.Ordinal)
        {
            [wireName] = group,
        };

    public static GroupCuration RootGroup() =>
        new()
        {
            Placement = GroupPlacement.Root,
            Reason = "Scenario places the group on the root client.",
        };

    public static GroupCuration ClientGroup(string clientName = "Sessions", string? handleName = "SessionClient",
        string? handleParameter = "sessionID", EmissionMode emission = EmissionMode.Public) =>
        new()
        {
            Placement = GroupPlacement.Client,
            ClientName = clientName,
            HandleName = handleName,
            HandleParameter = handleParameter,
            Emission = emission,
            Reason = "Scenario places the group on a family client.",
        };
}
