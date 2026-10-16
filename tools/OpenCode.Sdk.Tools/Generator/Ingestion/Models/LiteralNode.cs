namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents a schema constrained to one string, number, or boolean value.</summary>
internal sealed record LiteralNode : SchemaNode
{
    /// <summary>Gets the JSON primitive kind carried by the literal.</summary>
    public required LiteralKind Kind { get; init; }

    /// <summary>Gets the literal value in its deterministic textual form.</summary>
    public required string Value
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets the wire spelling used to express the literal.</summary>
    public required LiteralDialect Dialect { get; init; }

    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => [];
}
