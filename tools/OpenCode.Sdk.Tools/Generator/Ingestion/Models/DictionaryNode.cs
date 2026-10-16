namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents an object whose values share one schema.</summary>
internal sealed record DictionaryNode : SchemaNode
{
    /// <summary>Gets the projected dictionary value schema.</summary>
    public required SchemaNode Value
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => [Value];
}
