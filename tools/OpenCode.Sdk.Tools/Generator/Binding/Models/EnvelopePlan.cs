namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record EnvelopePlan
{
    public TypeDocumentationPlan Documentation { get; init; } = new();

    public required string ResponseTypeName { get; init; }

    public required string AdapterTypeName { get; init; }

    /// <summary>Gets the payload property name, or <see langword="null"/> for a no-content success.</summary>
    public required string? PayloadName { get; init; }

    /// <summary>
    /// Gets the payload type plan — for cursor lists and location lists the full list plan
    /// whose element is the item — or <see langword="null"/> for a no-content success.
    /// </summary>
    public required TypeReferencePlan? PayloadType { get; init; }

    public required EnvelopeKind Kind { get; init; }

    /// <summary>
    /// Gets the wire member the wrapped payload arrives under. The dialect's envelope key is
    /// <c>data</c>; a single-key body carries its payload under its own key instead.
    /// </summary>
    public string WireMemberName { get; init; } = "data";

    /// <summary>Gets the single declared success status the adapter accepts.</summary>
    public required int SuccessStatusCode { get; init; }

    /// <summary>
    /// Gets the internal single-pass deserialization DTO type name for wrapped envelopes,
    /// or <see langword="null"/> when the body is the payload itself or absent.
    /// </summary>
    public string? EnvelopeDtoTypeName { get; init; }

    /// <summary>
    /// Gets the type name of the required <c>location</c> envelope sibling, or
    /// <see langword="null"/> when the envelope carries none.
    /// </summary>
    public string? LocationTypeName { get; init; }
}
