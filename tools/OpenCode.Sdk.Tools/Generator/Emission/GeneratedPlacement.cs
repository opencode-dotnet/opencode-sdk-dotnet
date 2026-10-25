using OpenCode.Sdk.Tools.Generator.Binding.Models;

namespace OpenCode.Sdk.Tools.Generator.Emission;

/// <summary>
/// Owns generated paths and the path shapes admission accepts: each kind and owner's home map to a path,
/// so no emitter or validator spells a folder.
/// </summary>
internal static class GeneratedPlacement
{
    public const string ModelsFolder = "Models";

    /// <summary>Names a family or a model area cannot take; root Models/Pagination too, so no family recreates the old layout.</summary>
    public static IReadOnlyList<string> ReservedFolders { get; } = Array.AsReadOnly<string>(
        [TypeHome.CommonSlice, InternalFolder, "Hosting", "Properties", ModelsFolder, "Pagination"]);

    private const string InternalFolder = "Internal";
    private const string SharedSerializationFolder = $"{InternalFolder}/Serialization";
    private const string SharedErrorsFolder = $"{TypeHome.CommonSlice}/Errors";
    /// <summary>The shared homes generated files may live in beside the families.</summary>
    private static readonly string[] SharedFolders = [$"{SharedSerializationFolder}/", $"{SharedErrorsFolder}/", $"{TypeHome.CommonSlice}/{InternalFolder}/"];

    public static string PathOf(GeneratedKind kind, TypeHome? owner, string typeName)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(typeName);

        return kind switch
        {
            GeneratedKind.RootClient => $"{typeName}.cs",
            GeneratedKind.SharedSerialization => $"{SharedSerializationFolder}/{typeName}.cs",
            GeneratedKind.Routes => $"{Slice(kind, owner)}/{InternalFolder}/{typeName}.{Slice(kind, owner)}.cs",
            GeneratedKind.Model => $"{ModelFolder(kind, owner)}/{typeName}.cs",
            GeneratedKind.BodyRequest or GeneratedKind.OperationRecord or GeneratedKind.Client => $"{Slice(kind, owner)}/{typeName}.cs",
            GeneratedKind.RawClient or GeneratedKind.ModelConverter or GeneratedKind.EnvelopeDto or GeneratedKind.ResponseAdapter
                or GeneratedKind.StreamAdapter => $"{Slice(kind, owner)}/{InternalFolder}/{typeName}.cs",
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

    public static string ModelAreaFolder(TypeHome home)
    {
        ArgumentNullException.ThrowIfNull(home);
        return $"{home.Slice}/{ModelsFolder}/{home.Area}";
    }

    public static bool IsSharedPath(string relativePath) => SharedFolders.Any(folder =>
        relativePath.StartsWith(folder, StringComparison.Ordinal) && !relativePath[folder.Length..].Contains('/', StringComparison.Ordinal));

    /// <summary>A true answer still needs the caller to admit the family and, for a four-segment model path, its area.</summary>
    public static bool IsFamilyPath(IReadOnlyList<string> segments) => segments switch
    {
        [_, _] => true,
        [_, ModelsFolder or InternalFolder, _] => true,
        [_, ModelsFolder, _, _] => true,
        _ => false,
    };

    public static bool IsManifestPath(string relativePath)
    {
        var segments = relativePath.Split('/');
        return segments.Length switch
        {
            1 or 2 => true,
            3 => segments[1] is ModelsFolder or InternalFolder || IsSharedPath(relativePath),
            4 => segments[1] is ModelsFolder,
            _ => false,
        };
    }

    private static string Slice(GeneratedKind kind, TypeHome? owner) =>
        owner?.Slice ?? throw new InvalidOperationException($"A generated {kind} needs an owner.");

    /// <summary>
    /// A family's models live in its <c>Models/</c> folder, a Sessions model in its area's subfolder,
    /// and the shared error spine in <c>Common/Errors/</c>.
    /// </summary>
    private static string ModelFolder(GeneratedKind kind, TypeHome? owner)
    {
        var slice = Slice(kind, owner);
        if (owner!.IsCommon)
        {
            return SharedErrorsFolder;
        }

        return owner.Area is null ? $"{slice}/{ModelsFolder}" : ModelAreaFolder(owner);
    }
}
