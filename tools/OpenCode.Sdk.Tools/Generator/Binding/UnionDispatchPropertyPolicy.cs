using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Names every wire property a union's dispatch reads: its own marker, a nested union's marker,
/// and an outer marker it fixes. A shared literal that
/// discriminates is never a hoisted member — the interface already promises it as the marker,
/// and the dispatch path owns it.
/// </summary>
internal static class UnionDispatchPropertyPolicy
{
    public static IReadOnlySet<string> Collect(UnionPlan union, IReadOnlyDictionary<string, UnionPlan> unions)
    {
        ArgumentNullException.ThrowIfNull(union);
        ArgumentNullException.ThrowIfNull(unions);

        var result = new HashSet<string>(StringComparer.Ordinal);
        var visited = new HashSet<string>(StringComparer.Ordinal);
        Walk(union, unions, result, visited);
        for (var current = union.BaseTypeName; current is not null && unions.TryGetValue(current, out var outer);
             current = outer.BaseTypeName)
        {
            Walk(outer, unions, result, visited);
        }

        return result;
    }

    private static void Walk(UnionPlan union, IReadOnlyDictionary<string, UnionPlan> unions, HashSet<string> result,
        HashSet<string> visited)
    {
        if (!visited.Add(union.Name))
        {
            return;
        }

        _ = result.Add(union.MarkerWireName);
        foreach (var variant in union.Variants)
        {
            _ = result.Add(variant.MarkerWireName);
            if (variant.IsNestedUnion && unions.TryGetValue(variant.TypeName, out var nested))
            {
                Walk(nested, unions, result, visited);
            }
        }

        if (union.PrefixVariant is { } prefix)
        {
            _ = result.Add(prefix.MarkerWireName);
        }

        if (union.FixedMarker is { } fixedMarker)
        {
            _ = result.Add(fixedMarker.WireName);
        }
    }
}
