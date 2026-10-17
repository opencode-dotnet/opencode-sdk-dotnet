using System.IO.Abstractions;
using OpenCode.Sdk.Tools.Output.Abstractions;

namespace OpenCode.Sdk.Tools.Output;

internal sealed class GenerationWriter(
    IFileSystem fileSystem, PlacementValidator placement, ManifestStore manifests, StagedWrite write) : IGenerationWriter
{
    internal const string ManifestRelativePath = ManifestStore.ManifestRelativePath;
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly PlacementValidator _placement = placement ?? throw new ArgumentNullException(nameof(placement));
    private readonly ManifestStore _manifests = manifests ?? throw new ArgumentNullException(nameof(manifests));
    private readonly StagedWrite _write = write ?? throw new ArgumentNullException(nameof(write));

    public async Task<WriteResult> WriteAsync(GenerationWriteRequest request, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        var root = request.OutputRoot;
        var sources = _placement.ValidateSources(root, request.Sources, request.FamilyFolders, request.ModelAreaFolders);
        var oldPaths = await _manifests.LoadAsync(root, cancellationToken).ConfigureAwait(false);
        _placement.ValidateOverwrites(root, oldPaths, sources.Keys);
        await _manifests.RequireProvenanceAsync(root, oldPaths, cancellationToken).ConfigureAwait(false);
        var trackedPaths = oldPaths.Concat(sources.Keys)
            .Append(ManifestRelativePath)
            .Append(ManifestStore.MarkerRelativePath)
            .Append(ManifestStore.AttributesRelativePath)
            .Append(ManifestStore.ReachRelativePath)
            .Distinct(PlacementValidator.OwnedPathComparer).Order(StringComparer.Ordinal).ToArray();
        var before = await SnapshotAsync(root, trackedPaths, cancellationToken).ConfigureAwait(false);
        await _write.WriteAsync(request, oldPaths, sources, cancellationToken).ConfigureAwait(false);
        var after = await SnapshotAsync(root, trackedPaths, cancellationToken).ConfigureAwait(false);
        return Compare(before, after, request.Verify);
    }

    private async Task<Dictionary<string, byte[]?>> SnapshotAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var result = new Dictionary<string, byte[]?>(StringComparer.Ordinal);
        foreach (var relativePath in paths)
        {
            var path = _placement.ResolveOwnedPath(root, relativePath);
            result.Add(relativePath, _fileSystem.File.Exists(path)
                ? await _fileSystem.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false) : null);
        }

        return result;
    }

    private static WriteResult Compare(Dictionary<string, byte[]?> before, Dictionary<string, byte[]?> after, bool verify)
    {
        var created = new List<string>();
        var changed = new List<string>();
        var deleted = new List<string>();
        foreach (var relativePath in before.Keys.Order(StringComparer.Ordinal))
        {
            var oldContent = before[relativePath];
            var newContent = after[relativePath];
            if (oldContent is null && newContent is not null)
            {
                created.Add(relativePath);
            }
            else if (oldContent is not null && newContent is null)
            {
                deleted.Add(relativePath);
            }
            else if (oldContent is not null && newContent is not null && !oldContent.AsSpan().SequenceEqual(newContent))
            {
                changed.Add(relativePath);
            }
        }

        return new WriteResult
        {
            IsVerification = verify,
            CreatedPaths = created,
            ChangedPaths = changed,
            DeletedPaths = deleted,
        };
    }
}
