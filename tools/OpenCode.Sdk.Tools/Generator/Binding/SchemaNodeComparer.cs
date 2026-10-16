using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Structural equality over projected schema trees; node kinds without an explicit rule
/// compare unequal, so aliasing them refuses instead of guessing. Promoted inline schemas
/// carry no nominal identity, so their references compare by resolved structure while
/// nominal component references keep target identity. Object properties compare by name,
/// independently of declaration order.
/// </summary>
internal static class SchemaNodeComparer
{
    /// <param name="left">The first schema.</param>
    /// <param name="right">The second schema.</param>
    /// <param name="graph">The schema graph both nodes resolve references against.</param>
    /// <param name="aliases">
    /// Declared duplicates, collapsed before the comparison: identity is judged on the graph as
    /// it will be bound, so one alias can make a second pair identical. Undeclared names stay
    /// distinct, which is what keeps the alias an explicit act.
    /// </param>
    public static bool DeepEquals(SchemaNode left, SchemaNode right, IReadOnlyDictionary<string, SchemaNode> graph,
        IReadOnlyDictionary<string, string>? aliases = null)
    {
        ArgumentNullException.ThrowIfNull(left);
        ArgumentNullException.ThrowIfNull(right);
        ArgumentNullException.ThrowIfNull(graph);

        return new GraphComparison(graph, aliases).NodesEqual(left, right);
    }

    private sealed class GraphComparison(
        IReadOnlyDictionary<string, SchemaNode> graph,
        IReadOnlyDictionary<string, string>? aliases)
    {
        private readonly IReadOnlyDictionary<string, SchemaNode> _graph =
            graph ?? throw new ArgumentNullException(nameof(graph));

        private readonly IReadOnlyDictionary<string, string>? _aliases = aliases;

        private readonly HashSet<string> _visitedPairs = new(StringComparer.Ordinal);

        public bool NodesEqual(SchemaNode left, SchemaNode right)
        {
            // Format is type-affecting ('format: uri' emits Uri, not string), so it joins
            // identity ahead of the kind switch and rides every recursive comparison.
            if (!string.Equals(left.Format, right.Format, StringComparison.Ordinal))
            {
                return false;
            }

            return (left, right) switch
            {
                (PrimitiveNode a, PrimitiveNode b) => a.Kind == b.Kind,
                (LiteralNode a, LiteralNode b) => a.Kind == b.Kind && a.Dialect == b.Dialect
                                                                   && string.Equals(a.Value, b.Value, StringComparison.Ordinal),
                (EnumNode a, EnumNode b) => a.Values.SequenceEqual(b.Values, StringComparer.Ordinal),
                (RefNode a, RefNode b) => RefsEqual(a, b),
                (NullableNode a, NullableNode b) => NodesEqual(a.Inner, b.Inner),
                (ArrayNode a, ArrayNode b) => NodesEqual(a.Item, b.Item),
                (TupleNode a, TupleNode b) => SequencesEqual(a.Items, b.Items),
                (DictionaryNode a, DictionaryNode b) => NodesEqual(a.Value, b.Value),
                (UnionNode a, UnionNode b) => a.Keyword == b.Keyword && a.Classification == b.Classification
                                                                     && SequencesEqual(a.Branches, b.Branches),
                (ObjectNode a, ObjectNode b) => ObjectsEqual(a, b),
                (FreeFormObjectNode, FreeFormObjectNode) => true,
                (UnrestrictedNode, UnrestrictedNode) => true,
                (NeverNode, NeverNode) => true,
                _ => false,
            };
        }

        private bool RefsEqual(RefNode reference, RefNode other)
        {
            var left = Resolve(reference.Target);
            var right = Resolve(other.Target);
            if (string.Equals(left, right, StringComparison.Ordinal))
            {
                return true;
            }

            if (!left.Contains('#', StringComparison.Ordinal) || !right.Contains('#', StringComparison.Ordinal))
            {
                return false;
            }

            // A revisited pair is a cycle inside a comparison that has not failed yet.
            if (!_visitedPairs.Add(string.Concat(left, "\0", right)))
            {
                return true;
            }

            return _graph.TryGetValue(left, out var leftTarget)
                   && _graph.TryGetValue(right, out var rightTarget)
                   && NodesEqual(leftTarget, rightTarget);
        }

        private string Resolve(string target) =>
            _aliases is not null && _aliases.TryGetValue(target, out var aliased) ? aliased : target;

        private bool ObjectsEqual(ObjectNode left, ObjectNode right) =>
            left.AdditionalProperties == right.AdditionalProperties
            && left.ErrorStyle == right.ErrorStyle
            && AdditionalSchemasEqual(left, right)
            && left.Properties.Count == right.Properties.Count
            && left.Properties.OrderBy(static property => property.Name, StringComparer.Ordinal)
                .Zip(right.Properties.OrderBy(static property => property.Name, StringComparer.Ordinal))
                .All(pair => PropertiesEqual(pair.First, pair.Second))
            && left.LiteralMarkers.Count == right.LiteralMarkers.Count
            && left.LiteralMarkers.OrderBy(static marker => marker.PropertyName, StringComparer.Ordinal)
                .Zip(right.LiteralMarkers.OrderBy(static marker => marker.PropertyName, StringComparer.Ordinal))
                .All(static pair => MarkersEqual(pair.First, pair.Second))
            && left.PrefixMarkers.Count == right.PrefixMarkers.Count
            && left.PrefixMarkers.OrderBy(static marker => marker.PropertyName, StringComparer.Ordinal)
                .Zip(right.PrefixMarkers.OrderBy(static marker => marker.PropertyName, StringComparer.Ordinal))
                .All(static pair => pair.First == pair.Second);

        private bool AdditionalSchemasEqual(ObjectNode left, ObjectNode right) =>
            (left.AdditionalPropertiesSchema, right.AdditionalPropertiesSchema) switch
            {
                (null, null) => true,
                (not null, not null) => NodesEqual(left.AdditionalPropertiesSchema, right.AdditionalPropertiesSchema),
                _ => false,
            };

        private bool PropertiesEqual(SpecProperty left, SpecProperty right) =>
            string.Equals(left.Name, right.Name, StringComparison.Ordinal)
            && left.IsRequired == right.IsRequired
            && NodesEqual(left.Schema, right.Schema);

        private static bool MarkersEqual(LiteralMarker left, LiteralMarker right) =>
            string.Equals(left.PropertyName, right.PropertyName, StringComparison.Ordinal)
            && left.Kind == right.Kind
            && string.Equals(left.Value, right.Value, StringComparison.Ordinal);

        private bool SequencesEqual(IReadOnlyList<SchemaNode> left, IReadOnlyList<SchemaNode> right) =>
            left.Count == right.Count && left.Zip(right).All(pair => NodesEqual(pair.First, pair.Second));
    }
}
