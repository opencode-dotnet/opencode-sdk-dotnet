using System.IO.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>Coordinates receipt-governed prepare, observational verify and reviewed apply.</summary>
internal sealed class SnapshotSynchronizer(
    IFileSystem fileSystem,
    IProcessRunner processRunner,
    PatchSetLoader patchSetLoader,
    WatchedSourceReader watchedSourceReader,
    SnapshotGenerator generator,
    SnapshotStateReader stateReader,
    SnapshotVerifier verifier,
    SnapshotApplyPlanner planner,
    SnapshotApplyCommitter committer)
{
    public async Task<PrepareOutcome> PrepareAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var submodule = fileSystem.Path.GetFullPath(SnapshotPaths.Submodule);
        _ = await processRunner.RunCheckedAsync("git", ["fetch", "origin"], submodule, "fetching upstream", cancellationToken).ConfigureAwait(false);
        var state = await stateReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var symbolic = await processRunner.RunCheckedAsync("git", ["rev-parse", "--symbolic-full-name", reference], submodule,
            $"identifying '{reference}'", cancellationToken).ConfigureAwait(false);
        var namedRef = symbolic.StandardOutputText.Trim();
        var resolved = await processRunner.RunCheckedAsync("git", ["rev-parse", $"{reference}^{{commit}}"], submodule,
            $"resolving '{reference}'", cancellationToken).ConfigureAwait(false);
        var commit = resolved.StandardOutputText.Trim();
        if (!SnapshotStateReader.IsFullCommit(commit))
        {
            throw new SnapshotRefreshException($"'{reference}' did not resolve to a full commit SHA (got '{commit}')");
        }

        var raw = await processRunner.RunCheckedAsync("git", ["show", $"{commit}:{SnapshotPaths.UpstreamArtifact}"], submodule,
            "reading the raw artifact", cancellationToken).ConfigureAwait(false);
        var patches = await patchSetLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
        var watch = SourceWatchLoader.Parse(state.SourceWatch);
        var watchedSources = await watchedSourceReader.ObserveAsync(commit, watch.Sources, cancellationToken).ConfigureAwait(false);
        var scratchDirectory = fileSystem.Path.Combine(SnapshotPaths.ScratchRoot, commit);
        var generated = await generator.GenerateAsync(commit, scratchDirectory, patches, cancellationToken).ConfigureAwait(false);
        return await WriteScratchArtifactsAsync(new PreparedCandidate
        {
            Commit = commit,
            ReleaseTag = namedRef.StartsWith("refs/tags/", StringComparison.Ordinal) ? namedRef["refs/tags/".Length..] : null,
            ScratchDirectory = scratchDirectory,
            RawBytes = raw.StandardOutput,
            BaselineSha = generated.BaselineSha,
            NormalizedBytes = generated.NormalizedBytes,
            Patches = generated.Patches,
            WatchedSources = watchedSources,
        }, state, cancellationToken).ConfigureAwait(false);
    }

    public async Task<VerifyOutcome> VerifyAsync(CancellationToken cancellationToken)
    {
        if (!fileSystem.File.Exists(SnapshotPaths.CommittedReceipt))
        {
            return new VerifyOutcome
            {
                UpstreamCommit = "<none>",
                Problems = ["no committed receipt exists; the accepted snapshot predates the synchronizer"],
            };
        }

        return await verifier.VerifyAsync(await stateReader.ReadAsync(cancellationToken).ConfigureAwait(false), cancellationToken).ConfigureAwait(false);
    }

    public async Task<SnapshotReceipt> ApplyAsync(string receiptPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);
        var receipt = await stateReader.ReadReceiptAsync(receiptPath, cancellationToken).ConfigureAwait(false);
        // Fetch changes objects and refs, not accepted artifacts. Planning itself is read-only.
        _ = await processRunner.RunCheckedAsync("git", ["fetch", "origin"], fileSystem.Path.GetFullPath(SnapshotPaths.Submodule),
            "fetching upstream", cancellationToken).ConfigureAwait(false);
        var state = await stateReader.ReadAsync(cancellationToken).ConfigureAwait(false);
        var plan = await planner.PlanAsync(state, receipt, cancellationToken).ConfigureAwait(false);
        return await committer.CommitAsync(plan, cancellationToken).ConfigureAwait(false);
    }

    private async Task<PrepareOutcome> WriteScratchArtifactsAsync(PreparedCandidate candidate, SnapshotState state,
        CancellationToken cancellationToken)
    {
        var stats = DocumentInspector.Inspect(candidate.NormalizedBytes);
        var acceptedIds = DocumentInspector.Inspect(state.Document).OperationIds.ToHashSet(StringComparer.Ordinal);
        var candidateIds = stats.OperationIds.ToHashSet(StringComparer.Ordinal);
        _ = fileSystem.Directory.CreateDirectory(candidate.ScratchDirectory);
        var normalizedPath = fileSystem.Path.Combine(candidate.ScratchDirectory, "openapi.json");
        await fileSystem.File.WriteAllBytesAsync(normalizedPath, candidate.NormalizedBytes, cancellationToken).ConfigureAwait(false);
        var receipt = new SnapshotReceipt
        {
            SchemaVersion = SnapshotReceipt.CurrentSchemaVersion,
            ReleaseTag = candidate.ReleaseTag,
            UpstreamCommit = candidate.Commit,
            RawDocumentSha256 = DocumentInspector.Sha256Hex(candidate.RawBytes),
            GeneratedBaselineSha256 = candidate.BaselineSha,
            Patches = candidate.Patches,
            NormalizedDocumentSha256 = DocumentInspector.Sha256Hex(candidate.NormalizedBytes),
            NormalizedDocumentPath = normalizedPath,
            OperationSetDigest = stats.OperationSetDigest,
            OperationCount = stats.OperationIds.Count,
            AddedOperations = [.. stats.OperationIds.Where(id => !acceptedIds.Contains(id))],
            RemovedOperations = [.. acceptedIds.Where(id => !candidateIds.Contains(id)).Order(StringComparer.Ordinal)],
            ComponentCount = stats.ComponentCount,
            ContentSchemaCount = stats.ContentSchemaCount,
            WatchedSources = candidate.WatchedSources,
        };
        var receiptPath = fileSystem.Path.Combine(candidate.ScratchDirectory, "receipt.json");
        await fileSystem.File.WriteAllTextAsync(receiptPath,
            SnapshotStateReader.SerializeReceipt(receipt), cancellationToken).ConfigureAwait(false);
        return new PrepareOutcome { Receipt = receipt, ReceiptPath = receiptPath, NormalizedDocumentPath = normalizedPath };
    }

}
