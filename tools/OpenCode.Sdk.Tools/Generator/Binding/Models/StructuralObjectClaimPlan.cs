namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// What an object arm needs to see before it takes a JSON object that other object arms of the
/// same carrier also start with: every required key present, and every literal-constrained
/// property that is present holding one of its admitted values. The carrier tries the arms in
/// declaration order and the first claim that holds decides the member, as Effect's union decode
/// does before its deep parse.
/// </summary>
internal sealed record StructuralObjectClaimPlan
{
    /// <summary>Gets the required wire keys in document order.</summary>
    public required IReadOnlyList<string> RequiredKeys
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>Gets the literal-constrained properties in document order.</summary>
    public required IReadOnlyList<StructuralSentinelPlan> Sentinels
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }
}
