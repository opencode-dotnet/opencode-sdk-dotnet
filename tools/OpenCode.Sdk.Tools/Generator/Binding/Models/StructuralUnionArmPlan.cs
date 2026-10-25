using System.Text.Json;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record StructuralUnionArmPlan
{
    public required string Name { get; init; }

    public required TypeReferencePlan Type { get; init; }

    public required IReadOnlyList<JsonTokenType> Tokens
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>
    /// Gets the first-match claim of an object arm that shares <see cref="JsonTokenType.StartObject"/>
    /// with another object arm; null for every arm its token kind alone selects.
    /// </summary>
    public StructuralObjectClaimPlan? Claim { get; init; }
}
