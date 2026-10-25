namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>The bound plans after member hoisting: unions and records gain members, and the
/// promoted-record carriers gain their interfaces.</summary>
internal sealed record UnionHoistPlan
{
    public required IReadOnlyList<GeneratedTypeNameClaim> Claims { get; init; }

    public required IReadOnlyList<UnionPlan> Unions
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    public required IReadOnlyList<ModelPlan> Models
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    public required IReadOnlyList<HoistedInterfacePlan> Interfaces
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }
}
