namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record EnumModelPlan : ModelPlan
{
    public required IReadOnlyList<EnumValuePlan> Values
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }
}
