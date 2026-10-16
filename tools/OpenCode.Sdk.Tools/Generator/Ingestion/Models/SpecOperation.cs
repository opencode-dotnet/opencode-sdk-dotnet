namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Describes one projected OpenAPI operation.</summary>
internal sealed record SpecOperation
{
    /// <summary>Gets the verbatim operation identifier.</summary>
    public required string OperationId
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets the group and action segments of the operation identifier.</summary>
    public required IReadOnlyList<string> Segments
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>Gets the invariant-lowercase HTTP method.</summary>
    public required string Method
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets the verbatim path template.</summary>
    public required string Path
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = value;
        }
    }

    /// <summary>Gets a value indicating whether the path ends in a wildcard segment.</summary>
    public required bool HasWildcardPath { get; init; }

    /// <summary>Gets a value indicating whether the operation upgrades to a WebSocket.</summary>
    public required bool IsWebSocket { get; init; }

    /// <summary>Gets a value indicating whether any response is an event stream.</summary>
    public required bool IsSse { get; init; }

    /// <summary>Gets a value indicating whether the operation is deprecated.</summary>
    public required bool IsDeprecated { get; init; }

    /// <summary>Gets the operation summary, when supplied.</summary>
    public string? Summary { get; init; }

    /// <summary>Gets the operation description, when supplied.</summary>
    public string? Description { get; init; }

    /// <summary>Gets operation parameters in document order.</summary>
    public required IReadOnlyList<SpecParameter> Parameters
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<SpecParameter>());

    /// <summary>Gets the request body, when supplied.</summary>
    public SpecRequestBody? RequestBody { get; init; }

    /// <summary>Gets responses sorted by ascending status code.</summary>
    public required IReadOnlyList<SpecResponse> Responses
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<SpecResponse>());
}
