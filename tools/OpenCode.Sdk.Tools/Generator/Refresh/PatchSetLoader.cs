using System.IO.Abstractions;
using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>
/// Loads and validates the committed Restore-patch set under <c>spec/patches/</c>: every
/// manifest must name an existing, hash-matching patch file, carry its upstream report and
/// retirement condition, declare its touched files, and hold a supported repair predicate.
/// </summary>
internal sealed class PatchSetLoader(IFileSystem fileSystem)
{
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public async Task<IReadOnlyList<LoadedPatch>> LoadAsync(CancellationToken cancellationToken)
    {
        if (!_fileSystem.Directory.Exists(SnapshotPaths.PatchesRoot))
        {
            return Array.AsReadOnly(Array.Empty<LoadedPatch>());
        }

        var loaded = new List<LoadedPatch>();
        var manifestPaths = _fileSystem.Directory
            .GetFiles(SnapshotPaths.PatchesRoot, "*.json")
            .Order(StringComparer.Ordinal);
        foreach (var manifestPath in manifestPaths)
        {
            loaded.Add(await LoadOneAsync(manifestPath, cancellationToken).ConfigureAwait(false));
        }

        var orders = loaded.Select(static patch => patch.Manifest.Order).ToArray();
        if (orders.Distinct().Count() != orders.Length)
        {
            throw new SnapshotRefreshException("patch manifests declare duplicate order positions");
        }

        return Array.AsReadOnly([.. loaded.OrderBy(static patch => patch.Manifest.Order)]);
    }

    private async Task<LoadedPatch> LoadOneAsync(string manifestPath, CancellationToken cancellationToken)
    {
        var manifestName = _fileSystem.Path.GetFileName(manifestPath);
        PatchManifest? manifest;
        try
        {
            var stream = _fileSystem.File.OpenRead(manifestPath);
            await using (stream.ConfigureAwait(false))
            {
                manifest = await JsonSerializer
                    .DeserializeAsync(stream, ToolJsonContext.Default.PatchManifest, cancellationToken)
                    .ConfigureAwait(false);
            }
        }
        catch (JsonException exception)
        {
            throw new SnapshotRefreshException($"patch manifest '{manifestName}' is invalid: {exception.Message}", exception);
        }

        if (manifest is null)
        {
            throw new SnapshotRefreshException($"patch manifest '{manifestName}' cannot be JSON null");
        }

        Validate(manifestName, manifest);
        var patchPath = _fileSystem.Path.Combine(SnapshotPaths.PatchesRoot, manifest.Patch);
        if (!_fileSystem.File.Exists(patchPath))
        {
            throw new SnapshotRefreshException($"patch manifest '{manifestName}' names a missing patch file '{manifest.Patch}'");
        }

        var patchBytes = await _fileSystem.File.ReadAllBytesAsync(patchPath, cancellationToken).ConfigureAwait(false);
        var patchSha = DocumentInspector.Sha256Hex(patchBytes);
        if (!string.Equals(patchSha, manifest.Sha256, StringComparison.Ordinal))
        {
            throw new SnapshotRefreshException(
                $"patch file '{manifest.Patch}' does not match its manifest hash: expected {manifest.Sha256}, found {patchSha}");
        }

        return new LoadedPatch
        {
            ManifestName = manifestName,
            Manifest = manifest,
            PatchPath = patchPath,
        };
    }

    private static void Validate(string manifestName, PatchManifest manifest)
    {
        if (string.IsNullOrWhiteSpace(manifest.Patch))
        {
            throw new SnapshotRefreshException($"patch manifest '{manifestName}' must name its patch file");
        }

        if (string.IsNullOrWhiteSpace(manifest.UpstreamReport))
        {
            throw new SnapshotRefreshException($"patch manifest '{manifestName}' must carry its upstream report");
        }

        if (string.IsNullOrWhiteSpace(manifest.Retirement))
        {
            throw new SnapshotRefreshException($"patch manifest '{manifestName}' must state its retirement condition");
        }

        if (manifest.Touches.Count is 0 || manifest.Touches.Any(string.IsNullOrWhiteSpace))
        {
            throw new SnapshotRefreshException($"patch manifest '{manifestName}' must declare the files it touches");
        }

        var predicate = manifest.RepairPredicate;
        var complete = predicate.Type switch
        {
            PatchPredicate.ComponentLacksKeyword => predicate.Components is { Count: > 0 }
                && !predicate.Components.Any(string.IsNullOrWhiteSpace)
                && !string.IsNullOrWhiteSpace(predicate.Keyword),
            PatchPredicate.ParameterLacksStyle => predicate.Operations is { Count: > 0 }
                && !predicate.Operations.Any(string.IsNullOrWhiteSpace)
                && !string.IsNullOrWhiteSpace(predicate.Parameter)
                && !string.IsNullOrWhiteSpace(predicate.Style),
            _ => throw new SnapshotRefreshException(
                $"patch manifest '{manifestName}' declares unsupported predicate type '{predicate.Type}'"),
        };

        if (!complete)
        {
            throw new SnapshotRefreshException($"patch manifest '{manifestName}' declares an incomplete repair predicate");
        }
    }
}
