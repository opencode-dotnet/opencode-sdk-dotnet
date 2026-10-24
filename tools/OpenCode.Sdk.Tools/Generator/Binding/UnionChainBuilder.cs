using System.Collections.ObjectModel;
using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal static class UnionChainBuilder
{
    public static IReadOnlyList<UnionPlan> Build(IReadOnlyList<UnionPlan> unions)
    {
        ArgumentNullException.ThrowIfNull(unions);
        var byName = unions.ToDictionary(static union => union.Name, StringComparer.Ordinal);
        return Array.AsReadOnly(unions.Select(union => union with { Chain = Links(union, byName) }).ToArray());
    }

    public static EmitPlan Resolve(EmitPlan plan)
    {
        ArgumentNullException.ThrowIfNull(plan);
        var unions = Build(plan.Unions);
        var byName = unions.ToDictionary(static union => union.Name, StringComparer.Ordinal);
        return plan with
        {
            Unions = unions,
            Models = [.. plan.Models.Select(model => model is ObjectModelPlan concrete
                ? concrete with { ChainMarkers = Markers(concrete, byName) } : model)],
        };
    }

    private static ReadOnlyCollection<ChainMarkerPlan> Markers(ObjectModelPlan model, Dictionary<string, UnionPlan> unions)
    {
        var result = new List<ChainMarkerPlan>();
        var seen = new HashSet<string>(StringComparer.Ordinal);
        foreach (var link in model.ImplementedUnionNames.SelectMany(name => unions[name].Chain).Where(static link => !link.IsTerminal))
        {
            var wireName = link.Variants.FirstOrDefault(variant => string.Equals(variant.TypeName, model.Name, StringComparison.Ordinal))
                ?.MarkerWireName ?? link.MarkerWireName;
            if (seen.Add(wireName))
            {
                var prefix = link.PrefixVariant is { } arm
                    && string.Equals(arm.TypeName, model.Name, StringComparison.Ordinal)
                    && string.Equals(arm.MarkerWireName, wireName, StringComparison.Ordinal) ? arm.Prefix : null;
                result.Add(new ChainMarkerPlan(wireName, link.MarkerName, prefix));
            }
        }

        return result.AsReadOnly();
    }

    private static ReadOnlyCollection<UnionChainLink> Links(UnionPlan union, Dictionary<string, UnionPlan> unions)
    {
        var links = new List<UnionChainLink>();
        var current = union;
        for (var depth = 0; depth < unions.Count; depth++)
        {
            links.Add(new UnionChainLink(current.Name, current.MarkerWireName, current.MarkerName,
                current.MarkerKind, current.HoistedMembers, current.PrefixVariant, current.Variants, IsTerminal: false));
            if (current.BaseTypeName is not { } parent)
            {
                break;
            }

            if (unions.TryGetValue(parent, out var outer))
            {
                current = outer;
            }
            else
            {
                if (string.Equals(parent, EffectStreamTypeNamePolicy.CauseMarkerInterface, StringComparison.Ordinal))
                {
                    links.Add(new UnionChainLink(parent, ErrorMarkerPolicy.WireName, ErrorMarkerPolicy.MarkerMemberName,
                        LiteralKind.String, [], null, [], IsTerminal: true));
                }

                break;
            }
        }

        return links.AsReadOnly();
    }
}
