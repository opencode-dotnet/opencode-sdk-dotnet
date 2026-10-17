using System.IO.Abstractions;
using OpenCode.Sdk.Tools.Generator.Ingestion.Abstractions;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;
using OpenCode.Sdk.Tools.Generator.Ingestion.Projection;

namespace OpenCode.Sdk.Tools.Generator.Ingestion;

/// <summary>Composes the reader, projection, and raw-side validations into one ingestion pass.</summary>
internal sealed class SpecIngestion : ISpecIngestion
{
    private readonly SpecReader _reader;

    /// <summary>Creates the ingestion seam over the injected filesystem.</summary>
    public SpecIngestion(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        _reader = new SpecReader(fileSystem);
    }

    /// <summary>Ingests without operation-identity repairs; the production path supplies curation's map.</summary>
    public Task<SpecDocument> IngestAsync(string specPath, CancellationToken cancellationToken) =>
        IngestAsync(specPath, new Dictionary<string, string>(StringComparer.Ordinal), cancellationToken);

    /// <inheritdoc/>
    public async Task<SpecDocument> IngestAsync(string specPath, IReadOnlyDictionary<string, string> operationIdentities,
        CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(specPath);
        ArgumentNullException.ThrowIfNull(operationIdentities);

        var errors = new IngestionErrorCollector();
        var loaded = await _reader.LoadAsync(specPath, errors, cancellationToken).ConfigureAwait(false);

        var schemaProjector = new SchemaProjector();
        var state = new ProjectionState(errors, loaded.Document);
        var operationProjector = new OperationProjector(schemaProjector);

        var operations = operationProjector.Project(loaded, state, operationIdentities);
        if (loaded.Document.Components?.Schemas is { } namedSchemas)
        {
            foreach (var (name, schema) in namedSchemas)
            {
                _ = schemaProjector.Project(schema, GraphKeyBuilder.Root(name), string.Empty, state);
            }
        }

        RawSiblingScanner.Scan(loaded.Raw, errors);
        var schemas = state.SnapshotGraph();
        CheckDanglingReferences(operations, schemas, errors);
        errors.ThrowIfAny();

        return new SpecDocument
        {
            Operations = operations,
            Schemas = schemas,
        };
    }

    private static void CheckDanglingReferences(IReadOnlyList<SpecOperation> operations, IReadOnlyDictionary<string, SchemaNode> schemas,
        IngestionErrorCollector errors)
    {
        var roots = operations
            .SelectMany(static operation => operation
                .Parameters.Select(static parameter => parameter.Schema)
                .Concat(operation.Responses.Where(static response => response.Schema is not null).Select(static response => response.Schema!))
                .Append(operation.RequestBody?.Schema))
            .Concat(schemas.Values)
            .Where(static node => node is not null)
            .Select(static node => node!);

        foreach (var target in roots
                     .SelectMany(Descendants)
                     .OfType<RefNode>()
                     .Select(static reference => reference.Target)
                     .Where(target => !schemas.ContainsKey(target)))
        {
            errors.Add(target, "schema reference target does not exist in the projected graph");
        }
    }

    private static IEnumerable<SchemaNode> Descendants(SchemaNode node)
    {
        yield return node;
        foreach (var descendant in node.Children.SelectMany(Descendants))
        {
            yield return descendant;
        }
    }
}
