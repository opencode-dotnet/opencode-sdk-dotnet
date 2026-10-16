namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents a string whose content is JSON constrained by another schema.</summary>
internal sealed record JsonStringNode : SchemaNode
{
    /// <summary>Gets the schema for the JSON value encoded in the string.</summary>
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
