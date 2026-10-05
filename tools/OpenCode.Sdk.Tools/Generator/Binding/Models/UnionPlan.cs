using OpenCode.Sdk.Tools.Generator.Ingestion.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record UnionPlan
{
    /// <summary>Gets the emitted interface name, which is what every reference binds to.</summary>
    public required string Name { get; init; }

    /// <summary>Gets the union name without its interface prefix, which names the members around it.</summary>
    public required string ConceptName { get; init; }

    public required string Namespace { get; init; }

    public required string UnknownTypeName { get; init; }

    public required string MarkerWireName { get; init; }

    public required string MarkerName { get; init; }

    public required LiteralKind MarkerKind { get; init; }

    public required IReadOnlyList<UnionVariantPlan> Variants
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<UnionVariantPlan>());

    /// <summary>
    /// Gets the prefix-tagged arm, dispatched after every literal tag and before the unknown
    /// carrier; null for a union without one.
    /// </summary>
    public UnionPrefixVariantPlan? PrefixVariant { get; init; }

    /// <summary>
    /// Gets declared marker values whose branch schemas admit no JSON value. They remain
    /// known protocol input and must be refused rather than routed to the unknown carrier.
    /// </summary>
    public IReadOnlyList<string> KnownImpossibleTags
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>
    /// Gets the properties every member declares with an identical shape, promoted onto the
    /// interface. Each is declared nullable because the unknown carrier answers null for it.
    /// </summary>
    public IReadOnlyList<HoistedMemberPlan> HoistedMembers
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<HoistedMemberPlan>());

    public string? Description { get; init; }

    /// <summary>Gets the outer union base type when this union is itself a nested variant.</summary>
    public string? BaseTypeName { get; init; }

    /// <summary>Gets the outer marker this nested union fixes to one value for all its variants.</summary>
    public UnionFixedMarkerPlan? FixedMarker { get; init; }

    /// <summary>
    /// Gets whether the unknown carrier prints its preserved payload masked: a known arm carries a
    /// masked member at some depth, so an arm this pin does not know may carry the same secret.
    /// </summary>
    public bool MasksUnknownPayload { get; init; }
}
