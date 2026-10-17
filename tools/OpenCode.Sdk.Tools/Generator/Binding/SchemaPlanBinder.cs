using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class SchemaPlanBinder(
    StructuralUnionPlanBinder structuralUnions,
    UnionMembershipValidator unionMemberships,
    UnionDiscriminatorSelector discriminators,
    UnionMemberHoistBinder memberHoists)
{
    private readonly StringComparer _comparer = StringComparer.Ordinal;
    private readonly StructuralUnionPlanBinder _structuralUnions = structuralUnions
                                                                   ?? throw new ArgumentNullException(nameof(structuralUnions));
    private readonly UnionMembershipValidator _unionMemberships = unionMemberships
                                                                  ?? throw new ArgumentNullException(nameof(unionMemberships));
    private readonly UnionDiscriminatorSelector _discriminators = discriminators
                                                                  ?? throw new ArgumentNullException(nameof(discriminators));
    private readonly UnionMemberHoistBinder _memberHoists = memberHoists ?? throw new ArgumentNullException(nameof(memberHoists));

    public SchemaBindingResult Bind(SpecDocument document, ReachableSchemaSet reachable, GenerationCuration curation,
        IReadOnlyDictionary<string, string> typeNames, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(document);
        ArgumentNullException.ThrowIfNull(reachable);
        ArgumentNullException.ThrowIfNull(curation);
        ArgumentNullException.ThrowIfNull(typeNames);
        ArgumentNullException.ThrowIfNull(errors);

        var responseRoots = reachable.ResponseRootKeys.ToHashSet(_comparer);
        var requestKeys = reachable.RequestReachableKeys.ToHashSet(_comparer);
        var streamCauseKeys = reachable.StreamCauseKeys.ToHashSet(_comparer);
        var inhabitation = new SchemaInhabitationPolicy(document.Schemas);
        var typeBinder = new TypePlanBinder(document.Schemas, typeNames, errors);
        var context = new SchemaBindingContext(document, reachable, responseRoots, streamCauseKeys, typeNames, typeBinder, inhabitation, errors);
        var decisions = BindExplicitUnions(context, responseRoots);
        var errorUnion = new ErrorUnionBinder(context).Bind();
        decisions.Add(errorUnion.Union);
        var graph = UnionInheritanceGraph.Create(decisions, streamCauseKeys, errors);
        var objects = new ObjectModelBinder(context);
        var models = new List<ModelPlan>();
        foreach (var key in reachable.GraphKeys)
        {
            if (responseRoots.Contains(key) || !document.Schemas.TryGetValue(key, out var schema) || !typeNames.TryGetValue(key, out var name))
            {
                continue;
            }

            if (!inhabitation.IsInhabited(schema))
            {
                continue;
            }

            switch (schema)
            {
                case ObjectNode objectNode:
                    var objectPlan = objects.Bind(key, name, objectNode, graph.ImplementedUnionNames(key), requestKeys.Contains(key));
                    if (objectPlan is not null)
                    {
                        models.Add(objectPlan);
                    }

                    break;
                case EnumNode enumNode:
                    models.Add(BindEnum(key, name, enumNode, curation.EnumMemberNames, errors));
                    break;
                case UnionNode { Classification: UnionClassification.Structural } structural
                    when UnstructuredUnionPolicy.Collapse(structural, document.Schemas) is null:
                    var structuralPlan = _structuralUnions.Bind(key, name, structural, document.Schemas, typeBinder, errors);
                    if (structuralPlan is not null)
                    {
                        models.Add(structuralPlan);
                    }

                    break;
            }
        }

        var orderedModels = models.OrderBy(static model => model.Name, _comparer).ToArray();
        var orderedUnions = graph.Unions.OrderBy(static union => union.Name, _comparer).ToArray();
        _unionMemberships.Validate([.. orderedModels.OfType<ObjectModelPlan>()], orderedUnions, errors);
        // Hoisting adds members after membership validation. Its interfaces never enter the
        // registry because no converter or source-generated metadata materializes them.
        var hoisted = _memberHoists.Bind(orderedUnions, orderedModels, curation, errors);
        orderedModels = [.. hoisted.Models];
        orderedUnions = [.. hoisted.Unions];
        return new SchemaBindingResult
        {
            Models = orderedModels,
            Unions = orderedUnions,
            HoistedInterfaces = hoisted.Interfaces,
            ErrorUnion = errorUnion,
            Registry = new RegistryPlan
            {
                TypeNames = ComposeRegistryNames(orderedModels, orderedUnions),
            },
        };
    }

    private List<UnionBindingResult> BindExplicitUnions(SchemaBindingContext context, HashSet<string> responseRoots)
    {
        var markedBinder = new MarkedUnionBinder(context, _discriminators);
        var decisions = new List<UnionBindingResult>();
        foreach (var key in context.Reachable.GraphKeys)
        {
            if (!responseRoots.Contains(key)
                && context.Document.Schemas.TryGetValue(key, out var schema)
                && schema is UnionNode { Classification: UnionClassification.Marked } union
                && context.TypeNames.TryGetValue(key, out var name))
            {
                decisions.Add(markedBinder.Bind(key, name, union));
            }
        }

        return decisions;
    }

    /// <summary>The materializable types the serializer registry must carry, never hoisted interfaces.</summary>
    private string[] ComposeRegistryNames(IReadOnlyList<ModelPlan> models, IReadOnlyList<UnionPlan> unions) =>
    [
        .. models
            .Select(static model => model.Name)
            .Concat(models
                .OfType<StructuralUnionModelPlan>()
                .SelectMany(static model => model.Arms)
                .Where(static arm => arm.Type.IsCollection)
                .Select(static arm => TypeReferenceNamePolicy.Format(arm.Type)))
            .Concat(unions.SelectMany(static union => new[] { union.Name, union.UnknownTypeName, }))
            .Distinct(_comparer)
            .Order(_comparer),
    ];

    /// <summary>Each value's member name comes from <see cref="EnumMemberNamePolicy"/>.</summary>
    private static EnumModelPlan BindEnum(string key, string name, EnumNode node,
        IReadOnlyList<EnumMemberNameCuration> memberNames, BindingErrorCollector errors)
    {
        var values = node
            .Values.Select(value => new EnumValuePlan
            {
                Name = EnumMemberNamePolicy.Name(key, value, memberNames),
                WireValue = value,
            })
            .ToArray();
        var duplicate = values.GroupBy(static value => value.Name, StringComparer.Ordinal).FirstOrDefault(static group => group.Count() > 1);
        if (duplicate is not null)
        {
            errors.Add(BindingErrorCategory.Naming, name, $"multiple enum values map to C# name '{duplicate.Key}'");
        }

        // An enum value cannot take the enum's name or a member the extensible enum declares.
        foreach (var value in values.Where(value => ExtensibleEnumMemberPolicy.DeclaredNames.Contains(value.Name)
                                                    || string.Equals(value.Name, name, StringComparison.Ordinal)))
        {
            errors.Add(BindingErrorCategory.Naming, name,
                $"enum value '{value.WireValue}' maps to C# name '{value.Name}', which the generated enum declares itself; name the value through an enumMemberNames row");
        }

        return new EnumModelPlan
        {
            Name = name,
            Namespace = GeneratedNamespace.Models,
            Description = node.Description,
            Values = values,
        };
    }
}
