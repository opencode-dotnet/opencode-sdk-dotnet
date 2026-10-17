using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal sealed class UnionInheritanceGraph
{
    private readonly Dictionary<string, IReadOnlyList<string>> _memberships = new(StringComparer.Ordinal);

    private UnionInheritanceGraph() { }

    public IReadOnlyList<UnionPlan> Unions { get; private set; } = [];

    public IReadOnlyList<string> ImplementedUnionNames(string schemaKey) => _memberships.GetValueOrDefault(schemaKey) ?? [];

    public static UnionInheritanceGraph Create(IReadOnlyList<UnionBindingResult> results,
        IReadOnlySet<string> streamCauseKeys, BindingErrorCollector errors)
    {
        var edges = results.SelectMany(result => result.ChildEdges).ToLookup(edge => edge.SchemaKey, StringComparer.Ordinal);
        var graph = new UnionInheritanceGraph();
        foreach (var group in edges)
        {
            graph._memberships.Add(group.Key, Array.AsReadOnly(group.Select(edge => edge.ParentTypeName).Distinct(StringComparer.Ordinal).ToArray()));
        }

        var unions = new List<UnionPlan>();
        var refusedNames = new HashSet<string>(StringComparer.Ordinal);
        foreach (var result in results)
        {
            if (result.Plan is not { } plan)
            {
                continue;
            }

            if (result.SchemaKey is null)
            {
                unions.Add(plan);
                continue;
            }

            var parents = graph.ImplementedUnionNames(result.SchemaKey).ToList();
            if (streamCauseKeys.Contains(result.SchemaKey) && !parents.Contains(EffectStreamTypeNamePolicy.CauseMarkerInterface, StringComparer.Ordinal))
            {
                parents.Add(EffectStreamTypeNamePolicy.CauseMarkerInterface);
            }

            if (parents.Count > 1)
            {
                errors.Add(BindingErrorCategory.Schema, result.SchemaKey,
                    $"nested union has multiple parents {string.Join(" and ", parents.Select(parent => $"'{parent}'"))}");
                _ = refusedNames.Add(plan.Name);
                continue;
            }

            unions.Add(plan with
            {
                BaseTypeName = parents.SingleOrDefault(),
                FixedMarker = edges[result.SchemaKey].Select(edge => edge.FixedMarker).LastOrDefault(marker => marker is not null),
            });
        }

        // A refused parent's missing declaration is already diagnosed, not a new leaf error.
        foreach (var (key, names) in graph._memberships)
        {
            graph._memberships[key] = Array.AsReadOnly(names.Where(name => !refusedNames.Contains(name)).ToArray());
        }

        graph.Unions = unions.AsReadOnly();
        return graph;
    }
}
