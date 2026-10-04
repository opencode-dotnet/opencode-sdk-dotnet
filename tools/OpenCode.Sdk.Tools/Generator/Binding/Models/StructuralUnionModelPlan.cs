namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record StructuralUnionModelPlan : ModelPlan
{
    public required string KindTypeName { get; init; }

    public required IReadOnlyList<StructuralUnionArmPlan> Arms
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<StructuralUnionArmPlan>());

    /// <summary>
    /// Gets whether the printed form masks the Unknown arm's preserved token: a known arm carries a
    /// masked member at some depth, so a token this pin does not recognize may carry the same secret.
    /// </summary>
    public bool MasksUnknownPayload { get; init; }
}
