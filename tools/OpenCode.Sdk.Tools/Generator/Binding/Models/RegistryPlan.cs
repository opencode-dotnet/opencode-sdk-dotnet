namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

internal sealed record RegistryPlan
{
    public required IReadOnlyList<string> TypeNames
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    }

    /// <summary>
    /// Gets the bare container payload plans (lists and dictionaries not carried by an envelope
    /// DTO) that must join the serializer context directly, keyed by their own accessor name.
    /// </summary>
    public IReadOnlyList<TypeReferencePlan> PayloadEntries
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<TypeReferencePlan>());
}
