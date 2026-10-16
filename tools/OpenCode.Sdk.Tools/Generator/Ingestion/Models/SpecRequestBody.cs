namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Describes one projected operation request body.</summary>
internal sealed record SpecRequestBody
{
    /// <summary>Gets the request content type.</summary>
    public required SpecMediaType ContentType
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets the projected request schema.</summary>
    public required SchemaNode Schema
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets a value indicating whether the request body is required.</summary>
    public required bool IsRequired { get; init; }
}
