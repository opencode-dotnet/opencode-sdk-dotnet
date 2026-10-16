namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Describes a required literal property that identifies an object variant.</summary>
internal sealed record LiteralMarker
{
    /// <summary>Gets the opaque wire property name.</summary>
    public required string PropertyName
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets the JSON primitive kind carried by the marker.</summary>
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
}
