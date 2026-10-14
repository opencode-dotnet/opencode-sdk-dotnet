using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Which families' operations reach each generated type. A schema-backed type is reached when a
/// family's operations reach its schema; a union interface is reached wherever one of its arms is,
/// its unknown carrier and a structural union's kind wherever their union is, and a hoisted
/// interface wherever a record implementing it is. Reach is a report: ownership never reads it, so
/// a refresh that widens a type's reach changes the report and moves no file.
/// </summary>
internal static class ModelReachCollector
{
    public static IReadOnlyDictionary<string, IReadOnlyList<string>> Collect(ReachableSchemaCollector reachableSchemas, ReachScope scope,
        OwnershipCandidates candidates, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(reachableSchemas);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(errors);

        var familyOf = scope.Clients
            .SelectMany(static client => client.Operations)
            .ToDictionary(static operation => operation.OperationId, static operation => operation.RouteContainerName, StringComparer.Ordinal);
        var reach = new Dictionary<string, SortedSet<string>>(StringComparer.Ordinal);
        foreach (var family in scope.Selected
                     .Where(operation => familyOf.ContainsKey(operation.OperationId))
                     .GroupBy(operation => familyOf[operation.OperationId], StringComparer.Ordinal))
        {
            var reachable = reachableSchemas.Collect(scope.Document, [.. family], errors);
            foreach (var typeName in reachable.GraphKeys.Where(scope.TypeNames.ContainsKey).Select(key => scope.TypeNames[key]))
            {
                _ = Reached(reach, typeName).Add(family.Key);
            }
        }

        AddDerivedReach(candidates, reach);
        return reach.ToDictionary(static entry => entry.Key, static entry => (IReadOnlyList<string>)[.. entry.Value], StringComparer.Ordinal);
    }

    public static IReadOnlySet<string> CollectSubset(ReachableSchemaCollector reachableSchemas, ReachScope scope,
        OwnershipCandidates candidates, BindingErrorCollector errors)
    {
        ArgumentNullException.ThrowIfNull(reachableSchemas);
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(candidates);
        ArgumentNullException.ThrowIfNull(errors);

        var reachable = reachableSchemas.Collect(scope.Document, scope.Selected, errors);
        var reach = reachable.GraphKeys.Where(scope.TypeNames.ContainsKey).Select(key => scope.TypeNames[key])
            .Distinct(StringComparer.Ordinal)
            .ToDictionary(static name => name, static _ => new SortedSet<string>(StringComparer.Ordinal) { "subset" }, StringComparer.Ordinal);
        AddDerivedReach(candidates, reach);
        return reach.Where(static entry => entry.Value.Count > 0).Select(static entry => entry.Key).ToHashSet(StringComparer.Ordinal);
    }

    private static void AddDerivedReach(OwnershipCandidates candidates, Dictionary<string, SortedSet<string>> reach)
    {
        int previousCount;
        do
        {
            previousCount = reach.Values.Sum(static families => families.Count);
            foreach (var union in candidates.Unions)
            {
                var arms = union.Variants.Select(static variant => variant.TypeName)
                    .Concat(union.PrefixVariant is null ? [] : [union.PrefixVariant.TypeName]);
                var unionReach = Reached(reach, union.Name);
                foreach (var arm in arms.Where(reach.ContainsKey))
                {
                    unionReach.UnionWith(reach[arm]);
                }

                Reached(reach, union.UnknownTypeName).UnionWith(unionReach);
            }
        }
        while (reach.Values.Sum(static families => families.Count) != previousCount);

        foreach (var structural in candidates.Models.OfType<StructuralUnionModelPlan>())
        {
            Reached(reach, structural.KindTypeName).UnionWith(Reached(reach, structural.Name));
        }

        foreach (var hoisted in candidates.HoistedInterfaces)
        {
            var hoistedReach = Reached(reach, hoisted.Name);
            foreach (var record in hoisted.ImplementedBy.Where(reach.ContainsKey))
            {
                hoistedReach.UnionWith(reach[record]);
            }
        }
    }

    private static SortedSet<string> Reached(Dictionary<string, SortedSet<string>> reach, string typeName)
    {
        if (!reach.TryGetValue(typeName, out var families))
        {
            families = new SortedSet<string>(StringComparer.Ordinal);
            reach.Add(typeName, families);
        }

        return families;
    }
}
