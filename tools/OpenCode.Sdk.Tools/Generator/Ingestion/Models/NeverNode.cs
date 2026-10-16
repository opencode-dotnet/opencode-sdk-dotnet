namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents a schema that admits no JSON value.</summary>
internal sealed record NeverNode : SchemaNode
{
    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => [];
}
