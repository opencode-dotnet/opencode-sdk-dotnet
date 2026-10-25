namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents a fixed-position JSON array schema.</summary>
internal sealed record TupleNode : SchemaNode
{
    /// <summary>Gets the projected item schemas in positional order.</summary>
    public required IReadOnlyList<SchemaNode> Items
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => Items;
}
