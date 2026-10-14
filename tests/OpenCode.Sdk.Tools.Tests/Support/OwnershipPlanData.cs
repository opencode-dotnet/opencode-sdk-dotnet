using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using static OpenCode.Sdk.Tools.Tests.Support.BindingScenarioData;

namespace OpenCode.Sdk.Tools.Tests.Support;

/// <summary>
/// Bound-plan vocabulary for model ownership scenarios: the families the real curation admits, the
/// models, unions and hoisted interfaces to place, and the body-request operations that pin a model
/// to the family that sends it. Every group in the curation admits its family through one
/// operation, so a test varies only the names, rows and operations its rule is about.
/// </summary>
internal static class OwnershipPlanData
{
    /// <summary>The prefix alias the real curation gives the <c>LanguageModels</c> family.</summary>
    public const string ModelsAlias = "Models";

    /// <summary>The group key of the <c>LanguageModels</c> family.</summary>
    public const string ModelGroupKey = "model";

    private const string ModelNamespace = "OpenCode.Sdk.Models";
    private const string LanguageModels = "LanguageModels";

    /// <summary>
    /// The family groups the scenarios share, keyed and named as the real curation keys and names
    /// them. No group carries a prefix alias, because an alias that places nothing is an error.
    /// </summary>
    public static Dictionary<string, GroupCuration> Families() =>
        new(StringComparer.Ordinal)
        {
            ["session"] = FamilyGroup("Sessions"),
            ["credential"] = FamilyGroup("Credentials"),
            ["mcp"] = FamilyGroup("McpServers"),
            [ModelGroupKey] = FamilyGroup(LanguageModels),
            ["pty"] = FamilyGroup("Ptys"),
            ["persistentPty"] = FamilyGroup("PersistentPtys"),
            ["vcs"] = FamilyGroup("Vcs"),
            ["websearch"] = FamilyGroup("Websearch"),
            ["form"] = FamilyGroup("Forms"),
            ["example"] = FamilyGroup("Examples"),
        };

    /// <summary>
    /// The shared families with the <c>LanguageModels</c> group carrying <paramref name="alias"/> as its
    /// one prefix alias; the real curation's <see cref="ModelsAlias"/> when absent.
    /// </summary>
    public static Dictionary<string, GroupCuration> FamiliesWithModelAlias(string alias = ModelsAlias)
    {
        var groups = Families();
        groups[ModelGroupKey] = FamilyGroup(LanguageModels, alias);
        return groups;
    }

    /// <summary>
    /// Two families where the shorter prefix also matches the longer family's names at a word
    /// boundary; the shorter group is inserted first, so only the longest-first rule separates them.
    /// </summary>
    public static Dictionary<string, GroupCuration> PersistentFamilies() =>
        new(StringComparer.Ordinal)
        {
            ["persistent"] = FamilyGroup("Persistent"),
            ["persistentPty"] = FamilyGroup("PersistentPtys"),
        };

    public static GroupCuration FamilyGroup(string clientName, params string[] prefixAliases) =>
        ClientGroup(clientName, handleName: null, handleParameter: null) with { PrefixAliases = prefixAliases };

    public static GenerationCuration Ownership(IReadOnlyDictionary<string, GroupCuration>? groups = null,
        IReadOnlyList<ModelOwnerCuration>? owners = null, IReadOnlyList<SessionAreaCuration>? areas = null) =>
        Curation(groups ?? Families(), modelOwners: owners, sessionAreas: areas);

    public static SessionAreaCuration Area(string area, params string[] matches) =>
        new()
        {
            Area = area,
            Matches = matches,
            Reason = "The scenario groups the Sessions models its patterns name.",
        };

    public static ObjectModelPlan Model(string name) =>
        new()
        {
            Name = name,
            Namespace = ModelNamespace,
            Properties = [],
        };

    public static IReadOnlyList<ModelPlan> Models(params string[] names)
    {
        ArgumentNullException.ThrowIfNull(names);
        return [.. names.Select(Model)];
    }

    public static StructuralUnionModelPlan StructuralUnion(string name, string kindTypeName) =>
        new()
        {
            Name = name,
            Namespace = ModelNamespace,
            KindTypeName = kindTypeName,
            Arms = [],
        };

    public static UnionPlan Union(string conceptName) =>
        new()
        {
            Name = $"I{conceptName}",
            ConceptName = conceptName,
            Namespace = ModelNamespace,
            UnknownTypeName = $"Unknown{conceptName}",
            MarkerWireName = "type",
            MarkerName = "Type",
            MarkerKind = LiteralKind.String,
            Variants = [],
        };

    public static HoistedInterfacePlan HoistedInterface(string name) =>
        new()
        {
            Name = name,
            Namespace = ModelNamespace,
            ImplementedBy = [],
            Members = [],
        };

    /// <summary>An operation of <paramref name="family"/> that sends <paramref name="bodyTypeName"/> as its body.</summary>
    public static OperationPlan BodyOperation(string family, string bodyTypeName, bool isBinary = false) =>
        Operation(family, $"Send{bodyTypeName}") with
        {
            RequestBody = new RequestBodyPlan
            {
                TypeName = bodyTypeName,
                ParameterName = "request",
                IsOptional = false,
                IsBinary = isBinary,
            },
        };

    /// <summary>
    /// Places <paramref name="models"/> under <paramref name="curation"/> (the shared families when
    /// absent); each curated group admits its family through one bodiless operation beside
    /// <paramref name="bodyOperations"/>.
    /// </summary>
    public static ModelPlacement Place(BindingErrorCollector errors, IReadOnlyList<ModelPlan> models, GenerationCuration? curation = null,
        IReadOnlyList<UnionPlan>? unions = null, IReadOnlyList<OperationPlan>? bodyOperations = null,
        IReadOnlyList<HoistedInterfacePlan>? hoistedInterfaces = null)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(models);

        var ownership = curation ?? Ownership();
        var operations = ownership.Groups
            .Select(static entry => Operation(FamilyName(entry), "List"))
            .Concat(bodyOperations ?? []);
        var client = new ClientPlan
        {
            Name = "OpenCodeClient",
            Namespace = "OpenCode.Sdk",
            Role = ClientRole.Root,
            SubClients = [],
            Operations = [.. operations],
        };
        var candidates = new OwnershipCandidates
        {
            Models = models,
            Unions = unions ?? [],
            HoistedInterfaces = hoistedInterfaces ?? [],
        };
        return ModelOwnershipPolicy.Place(candidates, [client], ownership, errors);
    }

    /// <summary>Builds the prefix table of the families <paramref name="groups"/> (the shared families when absent) admit.</summary>
    public static FamilyPrefixTable PrefixTable(BindingErrorCollector errors, IReadOnlyDictionary<string, GroupCuration>? groups = null)
    {
        ArgumentNullException.ThrowIfNull(errors);

        var curation = Ownership(groups);
        var families = curation.Groups.Select(FamilyName).ToHashSet(StringComparer.Ordinal);
        return FamilyPrefixTable.Create(families, curation, errors);
    }

    private static string FamilyName(KeyValuePair<string, GroupCuration> group) =>
        group.Value.ClientName ?? CSharpNamePolicy.ToPascalCase(group.Key);

    private static OperationPlan Operation(string family, string memberName) =>
        new()
        {
            OperationId = $"{family}.{memberName}",
            MethodName = $"{memberName}Async",
            HttpMethod = "post",
            RouteTemplate = $"/{family}/{memberName}",
            RouteContainerName = family,
            RouteMemberName = memberName,
            Parameters = [],
            ErrorMap = new ErrorMapPlan { Statuses = [] },
        };
}
