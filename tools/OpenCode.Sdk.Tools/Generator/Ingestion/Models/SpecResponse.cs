namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Describes one projected operation response.</summary>
internal sealed record SpecResponse
{
    /// <summary>Gets the integer HTTP status code.</summary>
    public required int StatusCode { get; init; }

    /// <summary>Gets the response description, when supplied.</summary>
    public string? Description { get; init; }

    /// <summary>Gets the response content type, or <see langword="null"/> when there is no content.</summary>
    public SpecMediaType? ContentType { get; init; }

    /// <summary>Gets the projected response schema, when supplied.</summary>
    public SchemaNode? Schema { get; init; }

    /// <summary>Gets the classified response-envelope shape.</summary>
    public required SpecEnvelopeShape EnvelopeShape { get; init; }

    /// <summary>Gets a value indicating whether the response is an event stream.</summary>
    public required bool IsSse { get; init; }

    /// <summary>Gets the projected effect-stream contract, when supplied.</summary>
    public SpecEffectStreamContract? EffectStream { get; init; }
}
