namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents a projected string enum schema.</summary>
internal sealed record EnumNode : SchemaNode
{
    /// <summary>Gets the admitted string values in wire order.</summary>
    public required IReadOnlyList<string> Values
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => [];
}
