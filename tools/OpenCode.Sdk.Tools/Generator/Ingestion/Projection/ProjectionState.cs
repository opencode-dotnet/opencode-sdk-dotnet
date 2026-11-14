using System.Collections.ObjectModel;
using Microsoft.OpenApi;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

internal sealed class ProjectionState
{
    private readonly Dictionary<string, SchemaNode> _graph = [with(StringComparer.Ordinal)];
    private readonly HashSet<IOpenApiSchema> _visited = [with(ReferenceEqualityComparer.Instance)];

    public ProjectionState(IngestionErrorCollector errors, OpenApiDocument document)
    {
        ArgumentNullException.ThrowIfNull(errors);
        ArgumentNullException.ThrowIfNull(document);

        Errors = errors;
        Document = document;
    }

    public IngestionErrorCollector Errors { get; }

    public OpenApiDocument Document { get; }

    public SchemaNode? Register(SchemaNode? schema, string root, string pointer, string location)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);
        ArgumentNullException.ThrowIfNull(pointer);
        ArgumentException.ThrowIfNullOrWhiteSpace(location);

        if (schema is null)
        {
            return null;
        }

        switch (pointer.Length)
        {
            case > 0 when schema is EnumNode or ObjectNode or UnionNode:
                var promotedKey = $"{root}#{pointer}";

                if (_graph.TryAdd(promotedKey, schema))
                {
                    return new RefNode
                    {
                        Target = promotedKey,
                    };
                }

                Errors.Add(location, $"schema graph key collision for '{promotedKey}'");
                return null;

            case 0 when !_graph.TryAdd(root, schema):
                Errors.Add(location, $"schema graph key collision for '{root}'");
                return null;
            default:
                return schema;
        }
    }

    public bool TryVisit(IOpenApiSchema schema)
    {
        ArgumentNullException.ThrowIfNull(schema);
        return _visited.Add(schema);
    }

    public IReadOnlyDictionary<string, SchemaNode> SnapshotGraph()
    {
        var snapshot = _graph
            .OrderBy(pair => pair.Key, StringComparer.Ordinal)
            .ToDictionary(pair => pair.Key, pair => pair.Value, StringComparer.Ordinal);
        return new ReadOnlyDictionary<string, SchemaNode>(snapshot);
    }
}
