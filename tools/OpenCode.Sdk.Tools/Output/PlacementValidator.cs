using System.IO.Abstractions;
using OpenCode.Sdk.Tools.Generator.Emission;

namespace OpenCode.Sdk.Tools.Output;

/// <summary>Admits generated placement and protects paths that the manifest does not own.</summary>
internal sealed class PlacementValidator(IFileSystem fileSystem)
{
    internal static readonly StringComparer OwnedPathComparer = StringComparer.OrdinalIgnoreCase;

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public IReadOnlyDictionary<string, GeneratedSource> ValidateSources(string outputRoot, IReadOnlyList<GeneratedSource> sources,
        IReadOnlyList<string> familyFolders, IReadOnlyList<string> modelAreaFolders)
    {
        var families = ValidateFamilyFolders(familyFolders);
        var areas = ValidateAreaFolders(modelAreaFolders, families);
        var result = new Dictionary<string, GeneratedSource>(OwnedPathComparer);
        foreach (var source in sources)
        {
            if (source is null)
            {
                throw new ArgumentException("Generated sources cannot contain null entries.", nameof(sources));
            }

            _ = ResolveOwnedPath(outputRoot, source.RelativePath);
            if (!source.RelativePath.EndsWith(".cs", StringComparison.Ordinal)
                || !(!source.RelativePath.Contains('/', StringComparison.Ordinal)
                     || GeneratedPlacement.IsSharedPath(source.RelativePath) || IsAdmittedFamilyPath(source.RelativePath, families, areas)))
            {
                throw new OutputRefusalException($"Generated source path '{source.RelativePath}' is outside the owned source directories.");
            }

            // A headerless source would deadlock the next run's provenance requirement.
            if (!GenerationProvenance.HasHeader(source.Utf8Source.Span))
            {
                throw new InvalidOperationException($"Generated source '{source.RelativePath}' lacks the provenance header.");
            }

            if (!result.TryAdd(source.RelativePath, source))
            {
                throw new InvalidOperationException($"Generated source path '{source.RelativePath}' is duplicated.");
            }
        }

        return result;
    }

    public void ValidateOverwrites(string outputRoot, IReadOnlySet<string> oldPaths, IEnumerable<string> newPaths)
    {
        var owned = oldPaths.ToHashSet(OwnedPathComparer);
        foreach (var newPath in newPaths)
        {
            if (owned.TryGetValue(newPath, out var oldPath) && !string.Equals(oldPath, newPath, StringComparison.Ordinal))
            {
                throw new OutputRefusalException($"Generated source path '{newPath}' differs from owned path '{oldPath}' only by case.");
            }
        }

        // A new path that already exists belongs to somebody else until the manifest owns it.
        var overwrite = newPaths.Where(path => !owned.Contains(path)).Order(StringComparer.Ordinal)
            .FirstOrDefault(path => _fileSystem.File.Exists(ResolveOwnedPath(outputRoot, path)));
        if (overwrite is not null)
        {
            throw new OutputRefusalException($"Generated source path '{overwrite}' would overwrite an unmanifested file.");
        }
    }

    public string ResolveOwnedPath(string outputRoot, string relativePath)
    {
        if (string.IsNullOrWhiteSpace(relativePath) || _fileSystem.Path.IsPathRooted(relativePath)
            || relativePath.Contains('\\', StringComparison.Ordinal))
        {
            throw new OutputRefusalException($"Owned path '{relativePath}' is unsafe.");
        }

        var segments = relativePath.Split('/');
        if (segments.Any(static segment => string.IsNullOrWhiteSpace(segment) || segment is "." or ".."))
        {
            throw new OutputRefusalException($"Owned path '{relativePath}' is unsafe.");
        }

        var root = _fileSystem.Path.GetFullPath(outputRoot);
        var path = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine([root, .. segments]));
        var prefix = string.Concat(root.TrimEnd(_fileSystem.Path.DirectorySeparatorChar, _fileSystem.Path.AltDirectorySeparatorChar),
            _fileSystem.Path.DirectorySeparatorChar);
        var comparison = OperatingSystem.IsWindows() ? StringComparison.OrdinalIgnoreCase : StringComparison.Ordinal;
        if (!path.StartsWith(prefix, comparison))
        {
            throw new OutputRefusalException($"Owned path '{relativePath}' escapes the output root.");
        }

        var current = root;
        foreach (var segment in segments)
        {
            current = _fileSystem.Path.Combine(current, segment);
            if (_fileSystem.FileInfo.New(current).LinkTarget is not null)
            {
                throw new OutputRefusalException($"Owned path '{relativePath}' contains a symbolic link.");
            }
        }

        return path;
    }

    /// <summary>
    /// Checks shape, not family membership, so a retired family's sources stay deletable.
    /// The provenance requirement still guards every overwrite and delete.
    /// </summary>
    public static void ValidateManifestSourcePath(string relativePath)
    {
        if (!relativePath.EndsWith(".cs", StringComparison.Ordinal) || !GeneratedPlacement.IsManifestPath(relativePath))
        {
            throw new OutputRefusalException($"Generation manifest path '{relativePath}' is outside the owned source directories.");
        }
    }

    private static HashSet<string> ValidateFamilyFolders(IReadOnlyList<string> familyFolders)
    {
        // Case-insensitive collision checks protect Windows/macOS filesystems too.
        var result = new HashSet<string>(OwnedPathComparer);
        foreach (var folder in familyFolders)
        {
            if (string.IsNullOrWhiteSpace(folder) || folder is "." or ".."
                || folder.Contains('/', StringComparison.Ordinal) || folder.Contains('\\', StringComparison.Ordinal))
            {
                throw new OutputRefusalException($"Family folder '{folder}' is unsafe.");
            }

            if (GeneratedPlacement.ReservedFolders.Contains(folder, OwnedPathComparer))
            {
                throw new OutputRefusalException($"Family folder '{folder}' shadows a reserved folder name.");
            }

            if (!result.Add(folder))
            {
                throw new OutputRefusalException($"Family folder '{folder}' is duplicated.");
            }
        }

        return result;
    }

    private static HashSet<string> ValidateAreaFolders(IReadOnlyList<string> areaFolders, HashSet<string> familyFolders)
    {
        var result = new HashSet<string>(OwnedPathComparer);
        foreach (var folder in areaFolders)
        {
            if (folder.Split('/') is not [var family, GeneratedPlacement.ModelsFolder, var area]
                || !IsExactly(familyFolders, family) || string.IsNullOrWhiteSpace(area) || area is "." or ".."
                || area.Contains('\\', StringComparison.Ordinal) || GeneratedPlacement.ReservedFolders.Contains(area, OwnedPathComparer))
            {
                throw new OutputRefusalException($"Model area folder '{folder}' is not an area of an admitted family.");
            }

            if (!result.Add(folder))
            {
                throw new OutputRefusalException($"Model area folder '{folder}' is duplicated.");
            }
        }

        return result;
    }

    private static bool IsAdmittedFamilyPath(string relativePath, HashSet<string> families, HashSet<string> areas)
    {
        var segments = relativePath.Split('/');
        if (!IsExactly(families, segments[0]))
        {
            return false;
        }

        return GeneratedPlacement.IsFamilyPath(segments)
            && (segments.Length != 4 || IsExactly(areas, string.Join('/', segments[..3])));
    }

    /// <summary>Collision detection is case-insensitive, but admission needs the exact spelling.</summary>
    private static bool IsExactly(HashSet<string> set, string value) =>
        set.TryGetValue(value, out var actual) && string.Equals(actual, value, StringComparison.Ordinal);
}
