namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Describes one property of a projected object schema.</summary>
internal sealed record SpecProperty
{
    /// <summary>Gets the opaque wire property name.</summary>
    public required string Name
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets the projected property schema.</summary>
    public required SchemaNode Schema
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets a value indicating whether the property is required on the wire.</summary>
    public required bool IsRequired { get; init; }
}
