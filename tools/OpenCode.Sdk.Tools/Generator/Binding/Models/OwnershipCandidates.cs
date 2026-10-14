namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>The bound types the ownership rule places: models, union interfaces and hoisted interfaces.</summary>
internal sealed record OwnershipCandidates
{
    public required IReadOnlyList<ModelPlan> Models
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<ModelPlan>());

    public required IReadOnlyList<UnionPlan> Unions
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<UnionPlan>());

    public required IReadOnlyList<HoistedInterfacePlan> HoistedInterfaces
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<HoistedInterfacePlan>());
}
