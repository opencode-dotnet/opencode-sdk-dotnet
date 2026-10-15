using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// Decides which bound query properties the hand-written <c>ListRequest</c> base already
/// declares. The spine is recognized by inclusion: all three admitted members must be present
/// in their admitted shapes, while any further optional parameter binds beside them as its own
/// property, so a filtered list stays one request record with one continuation contract. A
/// missing member, or a member whose schema is not the admitted one, keeps the record flat.
/// </summary>
internal static class ListRequestSpinePolicy
{
    private static readonly string[] SpineWireNames = ["limit", "order", "cursor"];

    /// <summary>Reports whether the bound query carries every admitted spine member.</summary>
    public static bool CarriesSpine(IReadOnlyList<QueryPropertyPlan> properties)
    {
        ArgumentNullException.ThrowIfNull(properties);

        return Array.TrueForAll(
            SpineWireNames,
            wireName => properties.Any(property =>
                string.Equals(property.WireName, wireName, StringComparison.Ordinal) && IsSpineMember(property)));
    }

    /// <summary>Reports whether one bound property is the base's own declaration rather than a filter.</summary>
    public static bool IsSpineMember(QueryPropertyPlan property)
    {
        ArgumentNullException.ThrowIfNull(property);

        return property switch
        {
            { WireName: "limit", Kind: QueryValueKind.Int32, IsRequired: false } => true,
            { WireName: "cursor", Kind: QueryValueKind.Text, IsRequired: false } => true,
            { WireName: "order", Kind: QueryValueKind.ListOrder, IsRequired: false } => true,
            _ => false,
        };
    }
}
