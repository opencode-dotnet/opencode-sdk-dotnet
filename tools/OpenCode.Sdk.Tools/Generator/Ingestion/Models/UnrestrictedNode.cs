namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents a schema that admits any JSON value.</summary>
internal sealed record UnrestrictedNode : SchemaNode
{
    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => [];
}
