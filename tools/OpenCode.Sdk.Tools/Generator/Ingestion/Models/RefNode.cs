namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents a projected reference to another schema graph key.</summary>
internal sealed record RefNode : SchemaNode
{
    /// <summary>Gets the referenced schema graph key.</summary>
    public required string Target { get; init; }

    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => [];
}
