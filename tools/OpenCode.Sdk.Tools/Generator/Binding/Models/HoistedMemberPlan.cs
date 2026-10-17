namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// One property every member of a union — or every record behind a hoisted carrier — declares
/// with the same shape, promoted onto the interface so a consumer reads it without switching
/// over concrete types (ADR-0011).
/// </summary>
internal sealed record HoistedMemberPlan
{
    public required string WireName { get; init; }

    public required string Name { get; init; }

    /// <summary>Gets the source property's requiredness, identical in every variant because HoistedMemberIdentityPolicy.Identical compares IsRequired.</summary>
    public required bool IsRequired { get; init; }

    /// <summary>
    /// Gets the type the interface declares. On a union interface it is always nullable: the
    /// union's unknown carrier holds a raw payload and materializes no typed member (ADR-0009).
    /// </summary>
    public required TypeReferencePlan Type { get; init; }

    public string? Description { get; init; }
}
