using System.Collections.ObjectModel;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

internal static class EffectStreamRootNameResolver
{
    /// <summary>
    /// The extension gives its cause graph a shared semantic role independent of the operation
    /// carrying it. Distinct schemas claiming the same name hit the ordinary schema-name wall
    /// rather than silently merging.
    /// </summary>
    public static IReadOnlyDictionary<string, string> Resolve(SpecDocument document, IReadOnlyList<SpecOperation> selected,
        ClaimReferrerValidator referrers, BindingErrorCollector errors)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        var causes = selected.SelectMany(static operation => operation.Responses)
            .Select(static response => response.EffectStream?.CauseSchema).OfType<ArrayNode>().ToArray();
        foreach (var cause in causes)
        {
            if (cause.Item is not RefNode item
                || !document.Schemas.TryGetValue(item.Target, out var itemSchema)
                || itemSchema is not UnionNode { Classification: UnionClassification.Marked } union)
            {
                continue;
            }

            names[item.Target] = EffectStreamTypeNamePolicy.CauseInterface;
            foreach (var target in union.Branches.OfType<RefNode>().Select(static branch => branch.Target))
            {
                if (!document.Schemas.TryGetValue(target, out var branchSchema)
                    || branchSchema is not ObjectNode objectNode
                    || objectNode.LiteralMarkers.FirstOrDefault(static marker => marker.PropertyName is ErrorMarkerPolicy.WireName) is not { } marker)
                {
                    continue;
                }

                names[target] = EffectStreamTypeNamePolicy.CauseVariant(marker.Value);
            }
        }

        var branches = names.Where(static pair => pair.Value == EffectStreamTypeNamePolicy.CauseInterface)
            .SelectMany(pair => document.Schemas[pair.Key].Children).ToArray();
        foreach (var (key, name) in names)
        {
            var isUnion = name == EffectStreamTypeNamePolicy.CauseInterface;
            referrers.Check(key, isUnion ? "stream-cause union" : "stream-cause variant",
                isUnion ? causes.Select(static cause => cause.Item) : branches, names.Keys, errors);
        }

        return new ReadOnlyDictionary<string, string>(names);
    }
}
