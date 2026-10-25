using OpenCode.Sdk.Tools.Generator.Binding;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Tests.Support;

/// <summary>
/// Bound-plan vocabulary for member-hoisting scenarios: a marked union, the records its arms
/// bind to, and the promoted envelope records an arm points at. The builders speak the plan the
/// hoist binder reads, so a test states the shape difference it is about and nothing else.
/// </summary>
internal static class UnionHoistPlanData
{
    public static UnionHoistPlan HoistDurableCarrier(GenerationCuration curation)
    {
        var errors = new BindingErrorCollector();
        var arms = new[] { (Name: "CreatedEvent", Tag: "created", Version: "1"), (Name: "DeletedEvent", Tag: "deleted", Version: "2") };
        var result = UnionMemberHoistBinder.Bind(
            UnionChainFixture.Build([Union("IExampleEvent", [.. arms.Select(static arm => Arm(arm.Name))])]),
            [
                .. arms.Select(static arm => Variant(arm.Name, arm.Tag, Property("durable", Named(arm.Name + "Durable"), isRequired: true))),
                .. arms.Select(static arm => Envelope(arm.Name + "Durable", arm.Version)),
            ], curation, errors);
        CheckNames(result, errors);
        errors.ThrowIfAny();
        return result with { Unions = UnionChainFixture.Build(result.Unions) };
    }

    public static UnionPlan Union(string name, params UnionVariantPlan[] variants)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        ArgumentNullException.ThrowIfNull(variants);
        return new UnionPlan
        {
            Chain = [],
            Name = name,
            ConceptName = name[1..],
            Namespace = "OpenCode.Sdk.Models",
            UnknownTypeName = $"Unknown{name[1..]}",
            MarkerWireName = "type",
            MarkerName = "Type",
            MarkerKind = LiteralKind.String,
            Variants = variants,
        };
    }

    public static void CheckNames(UnionHoistPlan plan, BindingErrorCollector errors) =>
        OperationPlanBinder.CheckTypeNameCollisions(GeneratedTypeNameInventory.Create(new OwnershipCandidates
        {
            Models = plan.Models,
            Unions = plan.Unions,
            HoistedInterfaces = plan.Interfaces,
        }, [], plan.Claims, new Dictionary<string, string>(StringComparer.Ordinal)), [], errors);

    public static UnionVariantPlan Arm(string typeName) =>
        new()
        {
            TypeName = typeName,
            Tag = typeName,
            MarkerWireName = "type",
        };

    public static UnionVariantPlan NestedArm(string unionName) =>
        new()
        {
            TypeName = unionName,
            Tag = unionName,
            MarkerWireName = "type",
            IsNestedUnion = true,
        };

    /// <summary>A union arm's record: its literal tag plus the properties the scenario varies.</summary>
    public static ObjectModelPlan Variant(string typeName, string tag, params ModelPropertyPlan[] properties)
    {
        ArgumentNullException.ThrowIfNull(properties);
        return Record(typeName, [StringLiteral("type", tag), .. properties]);
    }

    public static ObjectModelPlan Record(string typeName, params ModelPropertyPlan[] properties) =>
        new()
        {
            Name = typeName,
            Namespace = "OpenCode.Sdk.Models",
            Properties = properties,
        };

    /// <summary>A promoted durable envelope: one plain member plus the per-arm schema-version literal.</summary>
    public static ObjectModelPlan Envelope(string typeName, string version) =>
        Record(typeName, Property("seq", Named("long"), isRequired: true), NumberLiteral("version", version));

    public static ModelPropertyPlan Property(string wireName, TypeReferencePlan type, bool isRequired,
        bool emitsOptionalWrapper = false) =>
        new()
        {
            WireName = wireName,
            Name = PascalCase(wireName),
            Type = type,
            IsRequired = isRequired,
            IsLiteral = false,
            EmitsOptionalWrapper = emitsOptionalWrapper,
        };

    public static ModelPropertyPlan StringLiteral(string wireName, string value) =>
        new()
        {
            WireName = wireName,
            Name = PascalCase(wireName),
            Type = Named("string"),
            IsRequired = true,
            IsLiteral = true,
            LiteralKind = LiteralKind.String,
            LiteralValue = value,
            EmitsOptionalWrapper = false,
        };

    public static ModelPropertyPlan NumberLiteral(string wireName, string value) =>
        new()
        {
            WireName = wireName,
            Name = PascalCase(wireName),
            Type = Named("double"),
            IsRequired = true,
            IsLiteral = true,
            LiteralKind = LiteralKind.Number,
            LiteralValue = value,
            EmitsOptionalWrapper = false,
        };

    public static NamedTypeReferencePlan Named(string name, bool isNullable = false) =>
        new()
        {
            Name = name,
            IsNullable = isNullable,
            JsonNullRepresentation = JsonNullRepresentation.ClrNull,
        };

    public static SpecialNumberTypeReferencePlan SpecialNumber(bool isNullable = false) =>
        new()
        {
            IsNullable = isNullable,
            JsonNullRepresentation = JsonNullRepresentation.ClrNull,
        };

    /// <summary>
    /// Runs the hoist binder over a scenario and wraps the result as the emit plan the source
    /// emitters read, for the cases whose proof is the rendered member rather than the bound plan.
    /// </summary>
    public static EmitPlan HoistedEmitPlan(IReadOnlyList<UnionPlan> unions, IReadOnlyList<ModelPlan> models,
        GenerationCuration curation)
    {
        ArgumentNullException.ThrowIfNull(unions);
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(curation);

        var errors = new BindingErrorCollector();
        var hoisted = UnionMemberHoistBinder.Bind(UnionChainFixture.Build(unions), models, curation, errors);
        CheckNames(hoisted, errors);
        errors.ThrowIfAny();
        return EmitPlanOf(hoisted);
    }

    /// <summary>Wraps hoisted unions and their models for emission with resolved chains and placement.</summary>
    public static EmitPlan EmitPlanOf(UnionHoistPlan hoisted)
    {
        ArgumentNullException.ThrowIfNull(hoisted);

        return UnionChainFixture.Resolve(new EmitPlan
        {
            Namespaces = new(GeneratedNamespace.Public, GeneratedNamespace.Models, GeneratedNamespace.Internal),
            ImplicitAliases = StabilizeDuplicateCollapse.Empty,
            SelectedOperationIds = [],
            Models = hoisted.Models,
            Unions = hoisted.Unions,
            HoistedInterfaces = hoisted.Interfaces,
            Clients = [],
            Registry = new RegistryPlan
            {
                TypeNames =
                [
                    .. hoisted
                        .Models.Select(static model => model.Name)
                        .Concat(hoisted.Unions.SelectMany(static union => new[] { union.Name, union.UnknownTypeName, }))
                        .Order(StringComparer.Ordinal),
                ],
            },
            PendingOperations = [],
            DeclinedOperations = [],
            TransportOwnedOperationIds = [],
        }.WithSingleSlice());
    }

    private static string PascalCase(string wireName) =>
        string.Concat(wireName[..1].ToUpperInvariant(), wireName[1..]);
}
