using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Tests.Support;

internal static class BindingScenarioData
{
    public static SpecScenario UnionScenario(Action<SpecDocumentBuilder> arrange) => SpecScenario.Define(spec =>
    {
        arrange(spec);
        spec.WithOperation("health.get", configure: operation => operation.Response(200, "application/json", schema => schema.Ref("Feed")));
    });

    public static async Task<BindingException> BindRefusalsAsync(SpecScenario scenario, GenerationCuration curation, params string[] operations)
    {
        var document = await BindingTestHost.IngestAsync(scenario);
        return Assert.Throws<BindingException>(() => new BindingTestHost().Bind(document, Selection(operations), curation));
    }

    public static Task<SpecDocument> ContractDocumentAsync() => BindingTestHost.IngestAsync(SpecScenario.Define(static spec =>
        spec.WithOperation("health.get", configure: static operation => operation.WithoutResponse(200).Response(204))));

    public static IReasonedRow ContractRow(string property) => property switch
    {
        "Descriptions" => new DescriptionCuration { Type = "subject", Summary = "Scenario summary.", Reason = "Scenario documentation." },
        "SessionScopedEvent" => new SessionScopedEventCuration { Unions = [], PayloadMember = "data", Member = "id", Identifier = "subject", Reason = "Scenario event rule." },
        "Groups" => RootGroup(),
        "OperationIdentities" => OperationIdentity("subject", "health.get"),
        "OperationNames" => OperationName("subject", "ReviewedAsync"),
        "ParameterNames" => ParameterName("subject", "parameter", "renamed"),
        "SchemaNames" => SchemaName("subject", "Reviewed"),
        "EnvelopePayloadNames" => EnvelopePayloadName("subject", "Reviewed"),
        "SchemaAliases" => Alias("subject", "target"),
        "TransportOwned" => TransportOwned("subject", new string('a', 64)),
        "HoistedMemberNames" => HoistedMemberName("subject", "property", "IReviewed"),
        "EnumMemberNames" => EnumMemberName("subject", "value", "Reviewed"),
        "RedactedMembers" => new RedactedMemberCuration { Model = "subject", Property = "property", Redact = true, Reason = "Scenario masks the member." },
        "RedactedOpenMembers" => OpenMembers("subject", redact: false),
        "SecretLookingNames" => new SecretLookingNameCuration { Property = "subject", Reason = "Scenario clears the name." },
        "Declined" => Declined("subject"),
        "ModelOwners" => Owner("subject", "Common"),
        "SessionAreas" => new SessionAreaCuration { Area = "subject", Matches = ["Subject"], Reason = "Scenario model area." },
        "QueryParameterTypes" => QueryParameterType("subject", "parameter", "int"),
        "StringQueryParameters" => StringQueryParameter("subject", "parameter"),
        _ => throw new InvalidOperationException("Unknown scenario row: " + property),
    };

    public static async Task<BindingException> BindUnsafeContractRowAsync(string propertyName, string? blankField)
    {
        var property = typeof(GenerationCuration).GetProperty(propertyName)!;
        var rows = Array.CreateInstance(property.PropertyType.GenericTypeArguments.Single(), 1);
        if (blankField is not null)
        {
            var row = ContractRow(propertyName);
            row.GetType().GetProperty(blankField)!.SetValue(row, " ");
            rows.SetValue(row, 0);
        }

        // A null collection element is an in-memory authoring error, not a nullable JSON member.
        return await BindContractRowsAsync(propertyName, rows);
    }

    public static BindingError IndexedContractError(string section, string problem) =>
        new(BindingErrorCategory.Curation, section + "[0]", section + "[0] " + problem);

    public static Task<BindingException> BindContractRowAsync(string propertyName, IReasonedRow row)
    {
        ArgumentNullException.ThrowIfNull(row);
        var rows = Array.CreateInstance(row.GetType(), 1);
        rows.SetValue(row, 0);
        var value = row switch
        {
            GroupCuration group => (object)Groups("subject", group),
            SessionScopedEventCuration => row,
            _ => rows,
        };
        return BindContractRowsAsync(propertyName, value);
    }

    private static async Task<BindingException> BindContractRowsAsync(string propertyName, object rows)
    {
        var curation = Curation(Groups("health", RootGroup()));
        typeof(GenerationCuration).GetProperty(propertyName)!.SetValue(curation, rows);
        var document = await ContractDocumentAsync();
        return Assert.Throws<BindingException>(() =>
        {
            _ = propertyName == "OperationIdentities" ? (object)OperationIdentityPolicy.BuildMap(curation)
                : new BindingTestHost().Bind(document, Selection("health.get"), curation);
        });
    }

    public static OperationSelection Selection(params string[] operationIds) =>
        new()
        {
            OperationIds = operationIds,
        };

    public static GenerationCuration Curation(IReadOnlyDictionary<string, GroupCuration> groups,
        IReadOnlyList<EnvelopePayloadNameCuration>? envelopePayloadNames = null,
        IReadOnlyList<SchemaAlias>? schemaAliases = null,
        IReadOnlyList<OperationNameCuration>? operationNames = null,
        IReadOnlyList<SchemaNameCuration>? schemaNames = null,
        IReadOnlyList<OperationIdentityCuration>? operationIdentities = null,
        IReadOnlyList<TransportOwnedCuration>? transportOwned = null,
        IReadOnlyList<DeclinedCuration>? declined = null,
        IReadOnlyList<HoistedMemberNameCuration>? hoistedMemberNames = null,
        IReadOnlyList<EnumMemberNameCuration>? enumMemberNames = null,
        IReadOnlyList<ParameterNameCuration>? parameterNames = null,
        IReadOnlyList<RedactedMemberCuration>? redactedMembers = null,
        IReadOnlyList<SecretLookingNameCuration>? secretLookingNames = null,
        IReadOnlyList<RedactedOpenMembersCuration>? redactedOpenMembers = null,
        IReadOnlyList<ModelOwnerCuration>? modelOwners = null,
        IReadOnlyList<SessionAreaCuration>? sessionAreas = null,
        SessionScopedEventCuration? sessionScopedEvent = null,
        IReadOnlyList<QueryParameterTypeCuration>? queryParameterTypes = null,
        IReadOnlyList<StringQueryParameterCuration>? stringQueryParameters = null) =>
        new()
        {
            Groups = groups,
            OperationIdentities = operationIdentities ?? [],
            OperationNames = operationNames ?? [],
            SchemaNames = schemaNames ?? [],
            EnvelopePayloadNames = envelopePayloadNames ?? [],
            SchemaAliases = schemaAliases ?? [],
            TransportOwned = transportOwned ?? [],
            Declined = declined ?? [],
            HoistedMemberNames = hoistedMemberNames ?? [],
            EnumMemberNames = enumMemberNames ?? [],
            ParameterNames = parameterNames ?? [],
            Descriptions = [],
            RedactedMembers = redactedMembers ?? [],
            RedactedOpenMembers = redactedOpenMembers ?? [],
            SecretLookingNames = secretLookingNames ?? [],
            ModelOwners = modelOwners ?? [],
            SessionAreas = sessionAreas ?? [],
            SessionScopedEvent = sessionScopedEvent,
            QueryParameterTypes = queryParameterTypes ?? [],
            StringQueryParameters = stringQueryParameters ?? [],
        };

    public static EnvelopePayloadNameCuration EnvelopePayloadName(string operationId, string name,
        string reason = "The scenario names the returned payload after its domain role.") =>
        new() { OperationId = operationId, Name = name, Reason = reason };

    public static ParameterNameCuration ParameterName(string operationId, string parameter, string dotnetName,
        string reason = "Scenario names the route value after its resource.") =>
        new()
        {
            OperationId = operationId,
            Parameter = parameter,
            DotNetName = dotnetName,
            Reason = reason,
        };

    public static QueryParameterTypeCuration QueryParameterType(string operationId, string parameter, string dotnetType,
        string reason = "Scenario: the server decodes the parameter as a number.",
        string evidencePath = "packages/protocol/src/groups/scenario.ts",
        string evidenceAnchor = "limit: Schema.NumberFromString") =>
        new()
        {
            OperationId = operationId,
            Parameter = parameter,
            DotNetType = dotnetType,
            Reason = reason,
            Evidence = new QueryParameterEvidence { Path = evidencePath, Anchor = evidenceAnchor },
        };

    public static StringQueryParameterCuration StringQueryParameter(string operationId, string parameter,
        string reason = "Scenario: the parameter is free text.") =>
        new()
        {
            OperationId = operationId,
            Parameter = parameter,
            Reason = reason,
        };

    public static ModelOwnerCuration Owner(string match, string owner,
        string reason = "The model carries no family prefix; the row places it with the family that owns its operations.") =>
        new()
        {
            Match = match,
            Owner = owner,
            Reason = reason,
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
        string? handleParameter = "sessionID", EmissionMode emission = EmissionMode.Public, string? experimentalDiagnostic = null) =>
        new()
        {
            Placement = GroupPlacement.Client,
            ClientName = clientName,
            HandleName = handleName,
            HandleParameter = handleParameter,
            Emission = emission,
            ExperimentalDiagnostic = experimentalDiagnostic,
            Reason = "Scenario places the group on a family client.",
        };
}
