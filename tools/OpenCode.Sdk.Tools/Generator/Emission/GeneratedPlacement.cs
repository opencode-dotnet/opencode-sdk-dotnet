using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// The one place a generated file's path is decided: its kind and its owner's home map to a path,
/// so no emitter spells a folder.
/// </summary>
internal static class GeneratedPlacement
{
    public static string PathOf(GeneratedKind kind, TypeHome? owner, string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);

        return kind switch
        {
            GeneratedKind.RootClient => $"{typeName}.cs",
            GeneratedKind.SharedSerialization => $"Internal/Serialization/{typeName}.cs",
            GeneratedKind.Routes => $"{Slice(kind, owner)}/Internal/{typeName}.{Slice(kind, owner)}.cs",
            GeneratedKind.Model => $"{ModelFolder(kind, owner)}/{typeName}.cs",
            GeneratedKind.BodyRequest or GeneratedKind.OperationRecord or GeneratedKind.Client => $"{Slice(kind, owner)}/{typeName}.cs",
            GeneratedKind.RawClient or GeneratedKind.ModelConverter or GeneratedKind.EnvelopeDto or GeneratedKind.ResponseAdapter
                or GeneratedKind.StreamAdapter => $"{Slice(kind, owner)}/Internal/{typeName}.cs",
            _ => throw new ArgumentOutOfRangeException(nameof(kind), kind, "Unknown generated kind."),
        };
    }

    /// <summary>Places a model, union member or hoisted interface by its own home; a body request sits with its operation's records.</summary>
    public static string ModelPathOf(ModelPlacement placement, string typeName)
    {
        ArgumentNullException.ThrowIfNull(placement);
        var kind = placement.BodyRequests.Contains(typeName, StringComparer.Ordinal) ? GeneratedKind.BodyRequest : GeneratedKind.Model;
        return PathOf(kind, placement.HomeOf(typeName), typeName);
    }

    /// <summary>Places a union or structural union converter with the union it serves.</summary>
    public static string ConverterPathOf(ModelPlacement placement, string unionName, string converterName)
    {
        ArgumentNullException.ThrowIfNull(placement);
        return PathOf(GeneratedKind.ModelConverter, placement.HomeOf(unionName), converterName);
    }

    /// <summary>
    /// A family's models live in its <c>Models/</c> folder, a Sessions model in its area's subfolder,
    /// and the shared error spine in <c>Common/Errors/</c>.
    /// </summary>
    private static string ModelFolder(GeneratedKind kind, TypeHome? owner)
    {
        var slice = Slice(kind, owner);
        if (owner!.IsCommon)
        {
            return $"{TypeHome.CommonSlice}/Errors";
        }

        return owner.Area is null ? $"{slice}/Models" : $"{slice}/Models/{owner.Area}";
    }

    private static string Slice(GeneratedKind kind, TypeHome? owner) =>
        owner?.Slice ?? throw new InvalidOperationException($"A generated {kind} needs an owner.");
}
