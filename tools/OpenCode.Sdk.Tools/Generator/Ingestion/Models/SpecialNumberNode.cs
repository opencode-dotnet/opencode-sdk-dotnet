namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents a number schema that also admits JSON string spellings for non-finite values.</summary>
internal sealed record SpecialNumberNode : SchemaNode
{
    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => [];
}
