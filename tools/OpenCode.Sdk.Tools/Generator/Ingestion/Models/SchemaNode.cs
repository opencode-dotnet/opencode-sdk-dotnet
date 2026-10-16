namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents one projected schema in the generator's supported schema dialect.</summary>
internal abstract record SchemaNode
{
    /// <summary>Gets the schema description, when supplied by the wire schema.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the schema format, when supplied by the wire schema.</summary>
    public string? Format { get; init; }

    /// <summary>Gets the schemas directly contained by this node.</summary>
    public abstract IEnumerable<SchemaNode> Children { get; }
}
