namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record SchemaBindingResult
{
    public required IReadOnlyList<ModelPlan> Models { get; init; }

    public required IReadOnlyList<UnionPlan> Unions { get; init; }

    public required IReadOnlyList<HoistedInterfacePlan> HoistedInterfaces { get; init; }

    public required RegistryPlan Registry { get; init; }

    public required ErrorUnionBindingResult ErrorUnion { get; init; }
}
