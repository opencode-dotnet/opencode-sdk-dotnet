namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Describes one projected operation parameter.</summary>
internal sealed record SpecParameter
{
    /// <summary>Gets the opaque wire parameter name.</summary>
    public required string Name
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets the wire location of the parameter.</summary>
    public required SpecParameterLocation Location { get; init; }

    /// <summary>Gets the projected parameter schema.</summary>
    public required SchemaNode Schema
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets a value indicating whether the parameter is required.</summary>
    public required bool IsRequired { get; init; }

    /// <summary>Gets a value indicating whether the parameter uses deep-object encoding.</summary>
    public required bool IsDeepObject { get; init; }
}
