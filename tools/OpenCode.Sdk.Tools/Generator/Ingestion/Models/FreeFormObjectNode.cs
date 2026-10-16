namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents an object whose property names and values are unrestricted.</summary>
internal sealed record FreeFormObjectNode : SchemaNode
{
    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => [];
}
