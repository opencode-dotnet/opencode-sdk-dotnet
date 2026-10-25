namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents a projected union of schema branches.</summary>
internal sealed record UnionNode : SchemaNode
{
    /// <summary>Gets the normalized branches in document order.</summary>
    public required IReadOnlyList<SchemaNode> Branches
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>Gets the OpenAPI keyword that declared the union.</summary>
    public required UnionKeyword Keyword { get; init; }

    /// <summary>Gets how the union branches can be distinguished.</summary>
    public required UnionClassification Classification { get; init; }

    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => Branches;
}
