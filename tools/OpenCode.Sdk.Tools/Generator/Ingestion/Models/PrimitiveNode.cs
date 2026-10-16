namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents a projected scalar primitive schema.</summary>
internal sealed record PrimitiveNode : SchemaNode
{
    /// <summary>Gets the projected primitive kind.</summary>
    public required PrimitiveKind Kind { get; init; }

    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => [];
}
