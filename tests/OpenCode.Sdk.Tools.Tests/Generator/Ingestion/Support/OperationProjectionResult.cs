using System.Collections.ObjectModel;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Support;

internal sealed record OperationProjectionResult
{
    public OperationProjectionResult(IReadOnlyList<SpecOperation> operations, IReadOnlyDictionary<string, SchemaNode> schemas,
        IngestionException? refusal)
    {
        ArgumentNullException.ThrowIfNull(operations);
        ArgumentNullException.ThrowIfNull(schemas);

        Operations = Array.AsReadOnly([.. operations]);
        Schemas = new ReadOnlyDictionary<string, SchemaNode>(new Dictionary<string, SchemaNode>(schemas, StringComparer.Ordinal));
        Refusal = refusal;
    }

    public IReadOnlyList<SpecOperation> Operations { get; }

    public IReadOnlyDictionary<string, SchemaNode> Schemas { get; }

    public IngestionException? Refusal { get; }
}
