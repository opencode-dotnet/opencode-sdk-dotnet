using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class SchemaPlanBinder(SchemaBindingContext context)
{
    private readonly SchemaBindingContext _context = context;

    public SchemaBindingResult Bind(GenerationCuration curation)
    {
        ArgumentNullException.ThrowIfNull(curation);

        var document = _context.Document;
        var reachable = _context.Reachable;
        var typeNames = _context.TypeNames;
        var errors = _context.Errors;
        var responseRoots = _context.ResponseRootKeys;
        var requestKeys = reachable.RequestReachableKeys.ToHashSet(StringComparer.Ordinal);
        var streamCauseKeys = _context.StreamCauseKeys;
        var inhabitation = _context.Inhabitation;
        var decisions = BindExplicitUnions();
        var errorUnion = new ErrorUnionBinder(_context).Bind();
        decisions.Add(errorUnion.Union);
        var graph = UnionInheritanceGraph.Create(decisions, streamCauseKeys, errors);
        var objects = new ObjectModelBinder(_context);
        var models = new List<ModelPlan>();
        foreach (var key in reachable.GraphKeys)
        {
            if (responseRoots.Contains(key) || !document.Schemas.TryGetValue(key, out var schema)
                || !typeNames.TryGetValue(key, out var name) || !inhabitation.IsInhabited(schema))
            {
                continue;
            }

            switch (schema)
            {
                case ObjectNode objectNode:
                    var objectPlan = objects.Bind(key, name, objectNode, graph.ImplementedUnionNames(key), requestKeys.Contains(key));
                    if (objectPlan is FacetResult<ObjectModelPlan>.Bound bound)
                    {
                        models.Add(bound.Value);
                    }
                    else if (objectPlan is FacetResult<ObjectModelPlan>.Refused refused)
                    {
                        errors.Append(refused.Errors);
                    }

                    break;
                case EnumNode enumNode:
                    models.Add(BindEnum(key, name, enumNode, curation.EnumMemberNames, errors));
                    break;
                case UnionNode { Classification: UnionClassification.Structural } structural
                    when UnstructuredUnionPolicy.Collapse(structural, document.Schemas) is null:
                    var structuralPlan = StructuralUnionPlanBinder.Bind(_context, key, name, structural);
                    if (structuralPlan is not null)
                    {
                        models.Add(structuralPlan);
                    }

                    break;
            }
        }

        var orderedModels = models.OrderBy(static model => model.Name, StringComparer.Ordinal).ToArray();
        var orderedUnions = graph.Unions.OrderBy(static union => union.Name, StringComparer.Ordinal).ToArray();
        UnionMembershipValidator.Validate([.. orderedModels.OfType<ObjectModelPlan>()], orderedUnions, errors);
        // Hoisting adds members after membership validation. Its interfaces never enter the
        // registry because no converter or source-generated metadata materializes them.
        var hoisted = UnionMemberHoistBinder.Bind(orderedUnions, orderedModels, curation, errors);
        orderedModels = [.. hoisted.Models];
        orderedUnions = [.. hoisted.Unions];
        return new SchemaBindingResult
        {
            TypeNames = typeNames,
            Models = orderedModels,
            Unions = orderedUnions,
            HoistedInterfaces = hoisted.Interfaces,
            HoistedClaims = hoisted.Claims,
            ErrorUnion = errorUnion,
            Registry = new RegistryPlan
            {
                TypeNames = ComposeRegistryNames(orderedModels, orderedUnions),
            },
        };
    }

    private List<UnionBindingResult> BindExplicitUnions()
    {
        var markedBinder = new MarkedUnionBinder(_context);
        var decisions = new List<UnionBindingResult>();
        foreach (var key in _context.Reachable.GraphKeys)
        {
            if (!_context.ResponseRootKeys.Contains(key)
                && _context.Document.Schemas.TryGetValue(key, out var schema)
                && schema is UnionNode { Classification: UnionClassification.Marked } union
                && _context.TypeNames.TryGetValue(key, out var name))
            {
                decisions.Add(markedBinder.Bind(key, name, union));
            }
        }

        return decisions;
    }

    /// <summary>The materializable types the serializer registry must carry, never hoisted interfaces.</summary>
    private static string[] ComposeRegistryNames(IReadOnlyList<ModelPlan> models, IReadOnlyList<UnionPlan> unions) =>
    [
        .. models
            .Select(static model => model.Name)
            .Concat(models
                .OfType<StructuralUnionModelPlan>()
                .SelectMany(static model => model.Arms)
                .Where(static arm => arm.Type.IsCollection)
                .Select(static arm => TypeReferenceNamePolicy.Format(arm.Type)))
            .Concat(unions.SelectMany(static union => new[] { union.Name, union.UnknownTypeName, }))
            .Distinct(StringComparer.Ordinal)
            .Order(StringComparer.Ordinal),
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
