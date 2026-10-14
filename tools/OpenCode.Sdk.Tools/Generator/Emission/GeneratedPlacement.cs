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
            GeneratedKind.Routes => $"{Slice(kind, owner)}/{typeName}.{Slice(kind, owner)}.cs",
            GeneratedKind.Model or GeneratedKind.BodyRequest => ModelPath(kind, owner, typeName),
            GeneratedKind.OperationRecord or GeneratedKind.Client or GeneratedKind.RawClient => $"{Slice(kind, owner)}/{typeName}.cs",
            GeneratedKind.ModelConverter or GeneratedKind.EnvelopeDto => InternalPath(kind, owner, "Internal/Serialization", typeName),
            GeneratedKind.ResponseAdapter => InternalPath(kind, owner, "Internal/ResponseAdapters", typeName),
            GeneratedKind.StreamAdapter => InternalPath(kind, owner, "Internal/StreamAdapters", typeName),
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

    private static string ModelPath(GeneratedKind kind, TypeHome? owner, string typeName)
    {
        _ = Slice(kind, owner);
        return $"Models/{typeName}.cs";
    }

    private static string InternalPath(GeneratedKind kind, TypeHome? owner, string sharedFolder, string typeName)
    {
        _ = Slice(kind, owner);
        return $"{sharedFolder}/{typeName}.cs";
    }

    private static string Slice(GeneratedKind kind, TypeHome? owner) =>
        owner?.Slice ?? throw new InvalidOperationException($"A generated {kind} needs an owner.");
}
