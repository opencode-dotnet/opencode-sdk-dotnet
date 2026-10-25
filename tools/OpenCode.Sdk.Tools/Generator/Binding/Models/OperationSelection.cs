namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record OperationSelection
{
    public required IReadOnlyList<string> OperationIds
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }
}
