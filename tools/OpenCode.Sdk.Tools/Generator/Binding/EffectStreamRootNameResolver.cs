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
    public static IReadOnlyDictionary<string, string> Resolve(SpecDocument document, IReadOnlyList<SpecOperation> selected)
    {
        var names = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var extension in selected
                     .SelectMany(static operation => operation.Responses)
                     .Select(static response => response.EffectStream)
                     .OfType<SpecEffectStreamContract>())
        {
            if (extension.CauseSchema is not ArrayNode { Item: RefNode item }
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

        return new ReadOnlyDictionary<string, string>(names);
    }
}
