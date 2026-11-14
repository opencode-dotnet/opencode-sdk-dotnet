using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Collects the graph keys a selected operation's request body reaches. Direction is the only
/// input the tri-state wrapper is keyed on: a property the document declares both optional and
/// nullable carries the wrapper exactly when its schema is reachable this way (ADR-0004).
/// </summary>
/// <remarks>
/// The walk keeps its own visited set rather than riding <see cref="ReachableSchemaCollector"/>'s
/// pass, which short-circuits at an already-visited key: a component first reached through a
/// response would otherwise never have its children marked. Refusals stay with that shared pass,
/// which visits every request body too, so this walk records keys and reports nothing.
/// </remarks>
internal sealed class RequestReachableSchemaWalker(IReadOnlyDictionary<string, SchemaNode> graph)
{
    private readonly IReadOnlyDictionary<string, SchemaNode> _graph = graph ?? throw new ArgumentNullException(nameof(graph));

    private readonly HashSet<string> _keys = [with(StringComparer.Ordinal)];

    public IReadOnlyList<string> Collect(IReadOnlyList<SpecOperation> operations)
    {
        ArgumentNullException.ThrowIfNull(operations);

        foreach (var body in operations.Select(static operation => operation.RequestBody).OfType<SpecRequestBody>())
        {
            Visit(body.Schema);
        }

        return Array.AsReadOnly([.. _keys.Order(StringComparer.Ordinal)]);
    }

    private void Visit(SchemaNode schema)
    {
        if (schema is RefNode reference)
        {
            if (!_keys.Add(reference.Target) || !_graph.TryGetValue(reference.Target, out var target))
            {
                return;
            }

            Visit(target);
            return;
        }

        // A structural union collapsed to one semantic value has no nominal branch models, so the
        // shared pass does not reach its promoted refinements either.
        if (schema is UnionNode { Classification: UnionClassification.Structural } union
            && UnstructuredUnionPolicy.Collapse(union, _graph) is not null)
        {
            return;
        }

        foreach (var child in schema.Children)
        {
            Visit(child);
        }
    }
}
