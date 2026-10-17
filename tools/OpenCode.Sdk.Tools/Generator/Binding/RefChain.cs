using OpenCode.Sdk.Tools.Generator.Binding.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>Follows references only, retaining the last successfully followed key even when a chain cannot finish.</summary>
internal static class RefChain
{
    public static RefChainResult Resolve(SchemaNode node, IReadOnlyDictionary<string, SchemaNode> graph)
    {
        ArgumentNullException.ThrowIfNull(node);
        ArgumentNullException.ThrowIfNull(graph);

        var visited = new HashSet<string>(StringComparer.Ordinal);
        string? lastKey = null;
        while (node is RefNode reference && visited.Add(reference.Target)
                                        && graph.TryGetValue(reference.Target, out var target))
        {
            lastKey = reference.Target;
            node = target;
        }

        return new RefChainResult(node, lastKey);
    }
}
