using System.IO.Abstractions;
using OpenCode.Sdk.Tools.Generator.Emission;
using OpenCode.Sdk.Tools.Output.Abstractions;

namespace OpenCode.Sdk.Tools.Output;

/// <summary>Publishes ownership before any source changes, then commits formatted output manifest-last.</summary>
internal sealed class StagedWrite(IFileSystem fileSystem, PlacementValidator placement, IProjectFormatter formatter)
{
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly PlacementValidator _placement = placement ?? throw new ArgumentNullException(nameof(placement));
    private readonly IProjectFormatter _formatter = formatter ?? throw new ArgumentNullException(nameof(formatter));

    public async Task WriteAsync(GenerationWriteRequest request, IReadOnlySet<string> oldPaths,
        IReadOnlyDictionary<string, GeneratedSource> sources, CancellationToken cancellationToken)
    {
        var root = request.OutputRoot;
        var paths = sources.Keys.Order(StringComparer.Ordinal).ToArray();
        await InstallAsync(root, ManifestStore.ManifestRelativePath,
            ManifestStore.ManifestBytes(oldPaths.Union(paths, PlacementValidator.OwnedPathComparer), request.ImplicitAliases),
            cancellationToken).ConfigureAwait(false);
        foreach (var path in paths)
        {
            await InstallAsync(root, path, sources[path].Utf8Source, cancellationToken).ConfigureAwait(false);
        }

        var retained = paths.ToHashSet(PlacementValidator.OwnedPathComparer);
        foreach (var path in oldPaths.Where(path => !retained.Contains(path)).Order(StringComparer.Ordinal))
        {
            Delete(root, path, cancellationToken);
        }

        await _formatter.FormatAsync(request.ProjectPath, paths, cancellationToken).ConfigureAwait(false);
        foreach (var (path, content) in ManifestStore.Metadata(request, paths))
        {
            if (content is null)
            {
                Delete(root, path, cancellationToken);
            }
            else
            {
                await InstallAsync(root, path, content, cancellationToken).ConfigureAwait(false);
            }
        }

        await InstallAsync(root, ManifestStore.ManifestRelativePath,
            ManifestStore.ManifestBytes(paths, request.ImplicitAliases), cancellationToken).ConfigureAwait(false);
    }

    private async Task InstallAsync(string root, string relativePath, ReadOnlyMemory<byte> content, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = _placement.ResolveOwnedPath(root, relativePath);
        var directory = _fileSystem.Path.GetDirectoryName(path)
                        ?? throw new InvalidOperationException($"Path '{path}' has no parent directory.");
        _fileSystem.Directory.CreateDirectory(directory);
        // Sibling payloads stay on the same filesystem and outside the project's C# glob.
        var temporaryRelativePath = $"{relativePath}.{Guid.NewGuid():N}.tmp";
        var temporaryPath = _placement.ResolveOwnedPath(root, temporaryRelativePath);
        try
        {
            var stream = _fileSystem.File.Open(temporaryPath, FileMode.Create, FileAccess.Write, FileShare.Read);
            await using (stream.ConfigureAwait(false))
            {
                await stream.WriteAsync(content, cancellationToken).ConfigureAwait(false);
            }

            cancellationToken.ThrowIfCancellationRequested();
            _fileSystem.File.Move(temporaryPath, path, true);
        }
        finally
        {
            _fileSystem.File.Delete(temporaryPath);
        }
    }

    private void Delete(string root, string relativePath, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();
        var path = _placement.ResolveOwnedPath(root, relativePath);
        if (_fileSystem.File.Exists(path))
        {
            _fileSystem.File.Delete(path);
        }
    }
}
