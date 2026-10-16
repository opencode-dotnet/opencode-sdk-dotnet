using System.IO.Abstractions;
using Microsoft.Extensions.Logging;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>Owns scratch worktree lifetime and the baseline and patched generator runs.</summary>
internal sealed partial class SnapshotGenerator(IFileSystem fileSystem, IProcessRunner processRunner, ILogger<SnapshotGenerator> logger)
{
    private readonly IFileSystem _fileSystem = fileSystem;
    private readonly IProcessRunner _processRunner = processRunner;

    public async Task<GeneratedSnapshot> GenerateAsync(
        string commit, string scratchDirectory, IReadOnlyList<LoadedPatch> patches, CancellationToken cancellationToken)
    {
        var worktree = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(scratchDirectory, "worktree"));
        var submodule = _fileSystem.Path.GetFullPath(SnapshotPaths.Submodule);
        if (_fileSystem.Directory.Exists(worktree))
        {
            await RemoveWorktreeAsync(worktree, submodule, cancellationToken).ConfigureAwait(false);
            if (_fileSystem.Directory.Exists(worktree))
            {
                throw new SnapshotRefreshException($"a stale worktree at '{worktree}' could not be removed");
            }
        }

        _ = await _processRunner.RunCheckedAsync(
                "git", ["worktree", "add", "--detach", worktree, commit], submodule, "adding the generation worktree", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            _ = await _processRunner.RunCheckedAsync(
                    "bun", ["install", "--frozen-lockfile", "--ignore-scripts"], worktree, "installing upstream packages",
                    cancellationToken)
                .ConfigureAwait(false);

            var protocolDirectory = _fileSystem.Path.Combine(worktree, SnapshotPaths.UpstreamProtocolPackage);
            var artifactPath = _fileSystem.Path.Combine(worktree, SnapshotPaths.UpstreamArtifact);
            var baselineBytes = await RunGeneratorAsync(protocolDirectory, artifactPath, "baseline", cancellationToken)
                .ConfigureAwait(false);
            var baselineSha = DocumentInspector.Sha256Hex(baselineBytes);

            // Retirement precedes preimages: upstream's fix may delete a touched file.
            CheckRepairPredicates(baselineBytes, patches);
            var receiptPatches = await HashPreimagesAsync(commit, submodule, patches, cancellationToken).ConfigureAwait(false);
            if (patches.Count is 0)
            {
                return new GeneratedSnapshot(baselineBytes, baselineSha, receiptPatches);
            }

            foreach (var patch in patches)
            {
                var patchPath = _fileSystem.Path.GetFullPath(patch.PatchPath);
                _ = await _processRunner.RunCheckedAsync(
                        "git", ["apply", patchPath], worktree, $"applying patch '{patch.ManifestName}'", cancellationToken)
                    .ConfigureAwait(false);
            }

            var normalizedBytes = await RunGeneratorAsync(protocolDirectory, artifactPath, "patched", cancellationToken)
                .ConfigureAwait(false);
            CheckRepairs(normalizedBytes, patches);
            return new GeneratedSnapshot(normalizedBytes, baselineSha, receiptPatches);
        }
        finally
        {
            // Cleanup must finish even after caller cancellation; scratch debris is never accepted state.
            try
            {
                await RemoveWorktreeAsync(worktree, submodule, CancellationToken.None).ConfigureAwait(false);
            }
            catch (Exception exception)
            {
                LogRemovalException(logger, worktree, exception);
            }
        }
    }

    private async Task RemoveWorktreeAsync(string worktree, string submodule, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync("git", ["worktree", "remove", "--force", worktree], submodule, cancellationToken)
            .ConfigureAwait(false);
        if (result.ExitCode is not 0)
        {
            LogRemovalFailure(logger, worktree, result.ExitCode, result.StandardError.Trim());
        }
    }

    [LoggerMessage(EventId = 1, Level = LogLevel.Warning, Message = "Removing scratch worktree '{Worktree}' failed (exit {ExitCode}): {StandardError}")]
    private static partial void LogRemovalFailure(ILogger logger, string worktree, int exitCode, string standardError);

    [LoggerMessage(EventId = 2, Level = LogLevel.Warning, Message = "Removing scratch worktree '{Worktree}' failed")]
    private static partial void LogRemovalException(ILogger logger, string worktree, Exception exception);

    private static void CheckRepairPredicates(byte[] baselineBytes, IReadOnlyList<LoadedPatch> patches)
    {
        foreach (var patch in patches)
        {
            var predicate = patch.Manifest.RepairPredicate;
            foreach (var component in predicate.Components)
            {
                switch (DocumentInspector.CheckComponentKeyword(baselineBytes, component, predicate.Keyword))
                {
                    case KeywordPresence.Carries:
                        throw new SnapshotRefreshException(
                            $"upstream's generator already satisfies patch '{patch.ManifestName}' (component '{component}' "
                            + $"carries '{predicate.Keyword}'); retire the patch with an empty-patch refresh");
                    case KeywordPresence.ComponentMissing:
                        throw new SnapshotRefreshException(
                            $"component '{component}' named by patch '{patch.ManifestName}' is absent from the generated "
                            + "baseline; the patch needs human review");
                    case KeywordPresence.Lacks:
                    default:
                        break;
                }
            }
        }
    }

    private static void CheckRepairs(byte[] normalizedBytes, IReadOnlyList<LoadedPatch> patches)
    {
        foreach (var patch in patches)
        {
            var predicate = patch.Manifest.RepairPredicate;
            var unrepaired = predicate.Components.FirstOrDefault(component =>
                DocumentInspector.CheckComponentKeyword(normalizedBytes, component, predicate.Keyword) is not KeywordPresence.Carries);
            if (unrepaired is not null)
            {
                throw new SnapshotRefreshException(
                    $"patch '{patch.ManifestName}' did not repair component '{unrepaired}' (it still lacks "
                    + $"'{predicate.Keyword}' after the patched generator run); the patch needs human review");
            }
        }
    }

    /// <summary>Delete the committed artifact first: a clean exit without output must refuse.</summary>
    private async Task<byte[]> RunGeneratorAsync(string protocolDirectory, string artifactPath, string run,
        CancellationToken cancellationToken)
    {
        _fileSystem.File.Delete(artifactPath);
        _ = await _processRunner.RunCheckedAsync("bun", ["run", "generate"], protocolDirectory, $"running the {run} generator", cancellationToken)
            .ConfigureAwait(false);
        if (!_fileSystem.File.Exists(artifactPath))
        {
            throw new SnapshotRefreshException(
                $"the {run} generator run exited cleanly but wrote no '{SnapshotPaths.UpstreamArtifact}'");
        }

        return await _fileSystem.File.ReadAllBytesAsync(artifactPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ReceiptPatch>> HashPreimagesAsync(string commit, string submodule, IReadOnlyList<LoadedPatch> patches,
        CancellationToken cancellationToken)
    {
        var receiptPatches = new List<ReceiptPatch>(patches.Count);
        foreach (var patch in patches)
        {
            var preimages = new List<ReceiptPreimage>(patch.Manifest.Touches.Count);
            foreach (var touched in patch.Manifest.Touches)
            {
                var blob = await _processRunner.RunAsync("git", ["show", $"{commit}:{touched}"], submodule, cancellationToken)
                    .ConfigureAwait(false);
                if (blob.ExitCode is not 0)
                {
                    throw new SnapshotRefreshException(
                        $"patch '{patch.ManifestName}' touches '{touched}', which does not exist at the target commit");
                }

                preimages.Add(new ReceiptPreimage
                {
                    Path = touched,
                    Sha256 = DocumentInspector.Sha256Hex(blob.StandardOutput),
                });
            }

            receiptPatches.Add(new ReceiptPatch
            {
                Manifest = patch.ManifestName,
                PatchSha256 = patch.Manifest.Sha256,
                Preimages = preimages,
            });
        }

        return receiptPatches;
    }

}
