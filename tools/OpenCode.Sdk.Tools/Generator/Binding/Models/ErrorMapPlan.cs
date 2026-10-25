namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record ErrorMapPlan
{
    public required IReadOnlyList<ErrorStatusPlan> Statuses
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }
}
