namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record ErrorStatusPlan
{
    public required int StatusCode { get; init; }

    public required IReadOnlyList<ErrorTagPlan> Tags
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }
}
