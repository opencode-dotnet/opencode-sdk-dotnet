using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Answers the bounded inhabitation question required by the pinned dialect: a never schema
/// makes a required object member and therefore that object impossible, while a union remains
/// inhabited when at least one branch is inhabited.
/// </summary>
internal sealed class SchemaInhabitationPolicy
{
    private readonly IReadOnlyDictionary<string, SchemaNode> _graph;

    public SchemaInhabitationPolicy(IReadOnlyDictionary<string, SchemaNode> graph)
    {
        _graph = graph ?? throw new ArgumentNullException(nameof(graph));
    }

    public bool IsInhabited(SchemaNode schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return IsInhabited(schema, [with(StringComparer.Ordinal)]);
    }

    private bool IsInhabited(SchemaNode schema, HashSet<string> visited) => schema switch
    {
        NeverNode => false,
        RefNode reference => IsReferenceInhabited(reference, visited),
        ObjectNode objectNode => objectNode
            .Properties
            .Where(static property => property.IsRequired)
            .All(property => IsInhabited(property.Schema, visited)),
        UnionNode union => union.Branches.Any(branch => IsInhabited(branch, visited)),
        NullableNode => true,
        TupleNode tuple => tuple.Items.All(item => IsInhabited(item, visited)),
        JsonStringNode jsonString => IsInhabited(jsonString.Inner, visited),
        _ => true,
    };

    private bool IsReferenceInhabited(RefNode reference, HashSet<string> visited)
    {
        if (!visited.Add(reference.Target))
        {
            return true;
        }

        var result = !_graph.TryGetValue(reference.Target, out var target) || IsInhabited(target, visited);
        _ = visited.Remove(reference.Target);
        return result;
    }
}
