namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Represents a projected object with named properties.</summary>
internal sealed record ObjectNode : SchemaNode
{
    /// <summary>Gets the named properties in document order.</summary>
    public required IReadOnlyList<SpecProperty> Properties
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>Gets how properties not declared by name are treated.</summary>
    public required AdditionalPropertiesKind AdditionalProperties { get; init; }

    /// <summary>Gets the additional-property schema when <see cref="AdditionalProperties"/> is <see cref="AdditionalPropertiesKind.Schema"/>.</summary>
    public SchemaNode? AdditionalPropertiesSchema { get; init; }

    /// <summary>Gets required literal properties in document order.</summary>
    public required IReadOnlyList<LiteralMarker> LiteralMarkers
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>
    /// Gets the required string properties constrained to a literal prefix, in document order;
    /// empty for every object that carries none.
    /// </summary>
    public IReadOnlyList<PrefixMarker> PrefixMarkers
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<PrefixMarker>());

    /// <summary>Gets the recognized error payload convention.</summary>
    public required ErrorStyle ErrorStyle { get; init; }

    /// <inheritdoc />
    public override IEnumerable<SchemaNode> Children => AdditionalPropertiesSchema is null
        ? Properties.Select(static property => property.Schema)
        : Properties.Select(static property => property.Schema).Append(AdditionalPropertiesSchema);
}
