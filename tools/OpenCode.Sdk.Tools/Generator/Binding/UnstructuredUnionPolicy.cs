using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Reads the two union shapes that offer a consumer no choice to make. Branches that are
/// refinements of one primitive differ only in constraints this SDK does not enforce, so the
/// union is that primitive — an open enum is the common case. An object-or-array pair is how
/// Effect renders its object keyword, and neither branch says what the content is, so it
/// is carried losslessly as JsonElement. Anything else stays a structural union.
/// </summary>
internal static class UnstructuredUnionPolicy
{
    public static SchemaNode? Collapse(UnionNode union, IReadOnlyDictionary<string, SchemaNode>? graph = null)
    {
        ArgumentNullException.ThrowIfNull(union);

        var kinds = union.Branches.Select(branch => PrimitiveKindOf(branch, graph, [])).ToArray();
        if (kinds.Length > 1 && kinds[0] is { } first && Array.TrueForAll(kinds, kind => kind == first))
        {
            return new PrimitiveNode
            {
                Kind = first
            };
        }

        return union.Branches.Count is 2
               && union.Branches.Count(static branch => branch is FreeFormObjectNode) is 1
               && union.Branches.Count(static branch => branch is ArrayNode { Item: UnrestrictedNode }) is 1
            ? new UnrestrictedNode()
            : null;
    }

    private static PrimitiveKind? PrimitiveKindOf(SchemaNode branch, IReadOnlyDictionary<string, SchemaNode>? graph, HashSet<string> visited)
    {
        if (branch is RefNode reference && graph is not null && visited.Add(reference.Target)
            && graph.TryGetValue(reference.Target, out var target))
        {
            return PrimitiveKindOf(target, graph, visited);
        }

        return branch switch
        {
            PrimitiveNode primitive => primitive.Kind,
            EnumNode => PrimitiveKind.String,
            LiteralNode { Kind: LiteralKind.String } => PrimitiveKind.String,
            LiteralNode { Kind: LiteralKind.Number } => PrimitiveKind.Number,
            LiteralNode { Kind: LiteralKind.Boolean } => PrimitiveKind.Boolean,
            _ => null,
        };
    }
}
