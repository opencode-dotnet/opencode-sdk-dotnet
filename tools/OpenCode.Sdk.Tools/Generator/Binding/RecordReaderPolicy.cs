using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Selects literal, closed record arms in the current union-reader rollout.</summary>
internal static class RecordReaderPolicy
{
    private static readonly IReadOnlySet<(string Union, string Variant)> Rollout = new HashSet<(string, string)>
    {
        ("ISessionMessageToolState", "SessionMessageToolStateCompleted"), ("ISessionMessageToolState", "SessionMessageToolStateError"),
        ("ISessionMessageToolState", "SessionMessageToolStateRunning"), ("ISessionMessageToolState", "SessionMessageToolStateStreaming"),
        ("IToolContent", "ToolFileContent"), ("IToolContent", "ToolTextContent"),
    };

    public static IReadOnlyDictionary<string, IReadOnlySet<string>> NestedCallSites { get; } =
        new Dictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);

    public static IReadOnlyDictionary<string, ObjectModelPlan> Select(IReadOnlyList<ModelPlan> models, IReadOnlyList<UnionPlan> unions,
        out IReadOnlyDictionary<string, IReadOnlySet<string>> readerArms, IReadOnlySet<(string Union, string Variant)>? rollout = null,
        IReadOnlyDictionary<string, IReadOnlySet<string>>? callSites = null)
    {
        ArgumentNullException.ThrowIfNull(models);
        ArgumentNullException.ThrowIfNull(unions);
        rollout ??= Rollout;
        var objects = models.OfType<ObjectModelPlan>().ToDictionary(static model => model.Name, StringComparer.Ordinal);
        var selected = new SortedDictionary<string, ObjectModelPlan>(StringComparer.Ordinal);
        var arms = new SortedDictionary<string, IReadOnlySet<string>>(StringComparer.Ordinal);
        foreach (var union in unions)
        {
            var names = new SortedSet<string>(StringComparer.Ordinal);
            arms.Add(union.Name, names);
            foreach (var name in union.Variants.Where(variant => rollout.Contains((union.Name, variant.TypeName))
                         && !variant.IsNestedUnion && variant.MarkerWireName == union.MarkerWireName)
                         .Select(static variant => variant.TypeName))
            {
                if (!objects.TryGetValue(name, out var model))
                {
                    throw new InvalidOperationException($"Selected reader arm '{union.Name}.{name}' has no bound object model.");
                }

                if (!model.EmitsExtensionData)
                {
                    selected[model.Name] = model;
                    names.Add(model.Name);
                }
            }
        }

        SelectNested(objects, selected, callSites ?? NestedCallSites);
        readerArms = arms;
        return selected;
    }

    private static void SelectNested(Dictionary<string, ObjectModelPlan> objects,
        SortedDictionary<string, ObjectModelPlan> selected, IReadOnlyDictionary<string, IReadOnlySet<string>> callSites)
    {
        var errors = new BindingErrorCollector();
        foreach (var (record, members) in callSites)
        {
            foreach (var member in members)
            {
                var property = objects.GetValueOrDefault(record)?.Properties.SingleOrDefault(property => property.Name == member);
                if (!selected.ContainsKey(record) || property?.Type is not NamedTypeReferencePlan named
                    || !objects.TryGetValue(named.Name, out var nested) || nested.EmitsExtensionData)
                {
                    errors.Add(BindingErrorCategory.Naming, record,
                        $"Nested reader call site '{record}.{member}' must name a direct closed-record member of a selected record.");
                    continue;
                }

                selected[nested.Name] = nested;
            }
        }
        errors.ThrowIfAny();
    }
}
