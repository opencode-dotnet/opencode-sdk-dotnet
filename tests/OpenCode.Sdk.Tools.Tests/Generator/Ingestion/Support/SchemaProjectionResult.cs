using System.Collections.ObjectModel;
using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Tests.Generator.Ingestion.Support;

internal sealed record SchemaProjectionResult
{
    public SchemaProjectionResult(IReadOnlyDictionary<string, SchemaNode> schemas, IngestionException? refusal)
    {
        ArgumentNullException.ThrowIfNull(schemas);

        Schemas = new ReadOnlyDictionary<string, SchemaNode>(new Dictionary<string, SchemaNode>(schemas, StringComparer.Ordinal));
        Refusal = refusal;
    }

    public IReadOnlyDictionary<string, SchemaNode> Schemas { get; }

    public IngestionException? Refusal { get; }
}
