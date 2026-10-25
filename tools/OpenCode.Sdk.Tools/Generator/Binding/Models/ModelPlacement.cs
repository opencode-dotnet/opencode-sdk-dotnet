using System.Collections.ObjectModel;

namespace OpenCode.Sdk.Tools.Generator.Binding.Models;

/// <summary>
/// Every generated model's slice, keyed by type name: models, union interfaces with their unknown
/// carriers, structural unions with their kind enums, and hoisted interfaces. Models no rule places
/// are listed in <see cref="Unplaced"/>; generation refuses while any remain.
/// </summary>
internal sealed record ModelPlacement
{
    public static ModelPlacement Empty { get; } = new() { Homes = new Dictionary<string, TypeHome>(StringComparer.Ordinal), };

    public required IReadOnlyDictionary<string, TypeHome> Homes
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = new ReadOnlyDictionary<string, TypeHome>(new Dictionary<string, TypeHome>(value, StringComparer.Ordinal));
        }
    }

    /// <summary>Gets the models neither a body-request operation, a union, an owner row nor a family prefix places, ordinal-sorted.</summary>
    public IReadOnlyList<string> Unplaced
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>Gets the body request models; they sit at their family's root beside the operation records, ordinal-sorted.</summary>
    public IReadOnlyList<string> BodyRequests
    {
        get;
        init
        {
            ArgumentNullException.ThrowIfNull(value);
            field = Array.AsReadOnly([.. value]);
        }
    } = Array.AsReadOnly(Array.Empty<string>());

    /// <summary>Gets the slice of a model, union, kit member or hoisted interface; an unplaced name refuses.</summary>
    public TypeHome HomeOf(string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);
        return Homes.TryGetValue(typeName, out var home)
            ? home
            : throw new InvalidOperationException($"Generated type '{typeName}' has no placement.");
    }
}
