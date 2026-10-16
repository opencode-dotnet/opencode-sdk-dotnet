namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents a projected schema that also admits JSON null.</summary>
internal sealed record NullableNode : SchemaNode
{
    /// <summary>Gets the projected non-null schema.</summary>
    public required SchemaNode Inner
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => [Inner];
}
