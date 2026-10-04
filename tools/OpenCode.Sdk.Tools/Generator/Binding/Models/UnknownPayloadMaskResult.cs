namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>The bound models and unions after the unknown-payload masks are decided.</summary>
internal sealed record UnknownPayloadMaskResult
{
    public required IReadOnlyList<ModelPlan> Models { get; init; }

    public required IReadOnlyList<UnionPlan> Unions { get; init; }
}
