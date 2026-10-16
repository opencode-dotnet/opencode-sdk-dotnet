namespace OpenCode.Sdk.Tools.Generator.Ingestion.Models;

/// <summary>Describes one projected <c>x-effect-stream</c> media extension.</summary>
internal sealed record SpecEffectStreamContract
{
    /// <summary>Gets the declared stream encoding, when supplied.</summary>
    public string? Encoding { get; init; }

    /// <summary>Gets the schema carried by the reserved failure frame, when supplied.</summary>
    public SchemaNode? CauseSchema { get; init; }

    /// <summary>Gets the stream's typed error schema, when supplied.</summary>
    public SchemaNode? ErrorSchema { get; init; }

    /// <summary>Gets the reserved event name used for a mid-stream failure, when supplied.</summary>
    public string? FailureEventName { get; init; }
}
