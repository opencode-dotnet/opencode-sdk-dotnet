using System.Globalization;
using System.IO.Abstractions;
using System.Text.Json;
using System.Text.RegularExpressions;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>
/// The receipt-governed snapshot synchronizer. Prepare resolves a moving reference once, produces
/// the normalized document by running the exact pinned upstream generator (over the ordered
/// Restore patches when there are any), and writes only scratch artifacts. Upstream's committed
/// artifact is read only to record whether it matches its own generator: upstream does not
/// regenerate it in CI, so it can lag the source it claims to describe. Verify reproduces the
/// accepted identity observationally. Apply is a human act over one reviewed receipt: it refuses
/// time-of-check/time-of-use drift, updates only the accepted snapshot paths and the submodule
/// checkout, and never stages, commits, or pushes. Every form also carries the source watch — the
/// upstream files the hand-written doors read as inputs — as a review trigger beside the
/// document: prepare observes it, verify checks the pins, apply re-pins over the reviewed receipt.
/// It never feeds generation.
/// </summary>
internal sealed partial class SnapshotSynchronizer(
    IFileSystem fileSystem,
    IProcessRunner processRunner,
    PatchSetLoader patchSetLoader,
    SourceWatchLoader sourceWatchLoader,
    WatchedSourceReader watchedSourceReader)
{
    private const int ReceiptSchemaVersion = 1;

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    private readonly PatchSetLoader _patchSetLoader = patchSetLoader ?? throw new ArgumentNullException(nameof(patchSetLoader));

    private readonly SourceWatchLoader _sourceWatchLoader =
        sourceWatchLoader ?? throw new ArgumentNullException(nameof(sourceWatchLoader));

    private readonly WatchedSourceReader _watchedSourceReader =
        watchedSourceReader ?? throw new ArgumentNullException(nameof(watchedSourceReader));

    public async Task<PrepareOutcome> PrepareAsync(string reference, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(reference);

        var submodule = _fileSystem.Path.GetFullPath(SnapshotPaths.Submodule);
        _ = await RunCheckedAsync("git", ["fetch", "origin"], submodule, "fetching upstream", cancellationToken)
            .ConfigureAwait(false);
        var resolved = await RunCheckedAsync(
                "git", ["rev-parse", $"{reference}^{{commit}}"], submodule, $"resolving '{reference}'", cancellationToken)
            .ConfigureAwait(false);
        var commit = resolved.StandardOutputText.Trim();
        if (!FullShaRegex().IsMatch(commit))
        {
            throw new SnapshotRefreshException($"'{reference}' did not resolve to a full commit SHA (got '{commit}')");
        }

        var raw = await RunCheckedAsync(
                "git", ["show", $"{commit}:{SnapshotPaths.UpstreamArtifact}"], submodule, "reading the raw artifact", cancellationToken)
            .ConfigureAwait(false);
        var rawBytes = raw.StandardOutput;
        var patches = await _patchSetLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
        var watch = await _sourceWatchLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
        var watchedSources = await _watchedSourceReader.ObserveAsync(commit, watch.Sources, cancellationToken).ConfigureAwait(false);

        var scratchDirectory = _fileSystem.Path.Combine(SnapshotPaths.ScratchRoot, commit);
        var (normalizedBytes, baselineSha, receiptPatches) =
            await GenerateAsync(commit, scratchDirectory, patches, cancellationToken).ConfigureAwait(false);

        return await WriteScratchArtifactsAsync(
                new PreparedCandidate
                {
                    Commit = commit,
                    ScratchDirectory = scratchDirectory,
                    RawBytes = rawBytes,
                    BaselineSha = baselineSha,
                    NormalizedBytes = normalizedBytes,
                    Patches = receiptPatches,
                    WatchedSources = watchedSources,
                },
                cancellationToken)
            .ConfigureAwait(false);
    }

    public async Task<VerifyOutcome> VerifyAsync(CancellationToken cancellationToken)
    {
        if (!_fileSystem.File.Exists(SnapshotPaths.CommittedReceipt))
        {
            return new VerifyOutcome
            {
                UpstreamCommit = "<none>",
                Problems = ["no committed receipt exists; the accepted snapshot predates the synchronizer"],
            };
        }

        var receipt = await ReadReceiptAsync(SnapshotPaths.CommittedReceipt, cancellationToken).ConfigureAwait(false);
        var problems = new List<string>();
        var acceptedBytes = await _fileSystem.File.ReadAllBytesAsync(SnapshotPaths.AcceptedDocument, cancellationToken)
            .ConfigureAwait(false);
        var acceptedSha = DocumentInspector.Sha256Hex(acceptedBytes);
        if (!string.Equals(acceptedSha, receipt.NormalizedDocumentSha256, StringComparison.Ordinal))
        {
            problems.Add($"accepted document hash {acceptedSha} does not match the receipt's {receipt.NormalizedDocumentSha256}");
        }

        CompareStats(DocumentInspector.Inspect(acceptedBytes), receipt, problems);

        var submodule = _fileSystem.Path.GetFullPath(SnapshotPaths.Submodule);
        var head = await RunCheckedAsync("git", ["rev-parse", "HEAD"], submodule, "reading the submodule checkout", cancellationToken)
            .ConfigureAwait(false);
        var checkedOut = head.StandardOutputText.Trim();
        if (!string.Equals(checkedOut, receipt.UpstreamCommit, StringComparison.Ordinal))
        {
            problems.Add($"submodule checkout {checkedOut} does not match the receipt's {receipt.UpstreamCommit}");
        }

        try
        {
            _ = await _patchSetLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
        }
        catch (SnapshotRefreshException exception)
        {
            problems.Add(exception.Message);
        }

        problems.AddRange(await CheckWatchedSourcesAsync(receipt, cancellationToken).ConfigureAwait(false));
        return new VerifyOutcome
        {
            UpstreamCommit = receipt.UpstreamCommit,
            Problems = problems,
        };
    }

    public async Task<SnapshotReceipt> ApplyAsync(string receiptPath, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(receiptPath);

        if (!_fileSystem.File.Exists(receiptPath))
        {
            throw new SnapshotRefreshException($"receipt '{receiptPath}' does not exist");
        }

        var receipt = await ReadReceiptAsync(receiptPath, cancellationToken).ConfigureAwait(false);
        if (receipt.SchemaVersion is not ReceiptSchemaVersion)
        {
            throw new SnapshotRefreshException(
                $"receipt schema version {receipt.SchemaVersion.ToString(CultureInfo.InvariantCulture)} is not supported");
        }

        if (!FullShaRegex().IsMatch(receipt.UpstreamCommit))
        {
            throw new SnapshotRefreshException($"receipt upstream commit '{receipt.UpstreamCommit}' is not a full SHA");
        }

        if (string.IsNullOrEmpty(receipt.GeneratedBaselineSha256))
        {
            throw new SnapshotRefreshException(
                "the receipt records no generator baseline, so its document may be upstream's committed copy; re-run prepare");
        }

        if (receipt.NormalizedDocumentPath is null || !_fileSystem.File.Exists(receipt.NormalizedDocumentPath))
        {
            throw new SnapshotRefreshException("the receipt carries no prepared document; re-run prepare");
        }

        var normalizedBytes = await _fileSystem.File.ReadAllBytesAsync(receipt.NormalizedDocumentPath, cancellationToken)
            .ConfigureAwait(false);
        var normalizedSha = DocumentInspector.Sha256Hex(normalizedBytes);
        if (!string.Equals(normalizedSha, receipt.NormalizedDocumentSha256, StringComparison.Ordinal))
        {
            throw new SnapshotRefreshException(
                "the prepared document changed after the receipt was written (time-of-check/time-of-use drift); re-run prepare");
        }

        var driftProblems = new List<string>();
        CompareStats(DocumentInspector.Inspect(normalizedBytes), receipt, driftProblems);
        if (driftProblems.Count > 0)
        {
            throw new SnapshotRefreshException($"the prepared document disagrees with its receipt: {string.Join("; ", driftProblems)}");
        }

        var watch = await _sourceWatchLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
        var repinned = SourceWatchRepinner.Repin(watch, receipt.WatchedSources);

        var submodule = _fileSystem.Path.GetFullPath(SnapshotPaths.Submodule);
        _ = await RunCheckedAsync("git", ["fetch", "origin"], submodule, "fetching upstream", cancellationToken).ConfigureAwait(false);
        _ = await RunCheckedAsync(
                "git", ["rev-parse", "--verify", $"{receipt.UpstreamCommit}^{{commit}}"], submodule,
                "verifying the receipt commit exists", cancellationToken)
            .ConfigureAwait(false);
        _ = await RunCheckedAsync(
                "git", ["checkout", "--detach", receipt.UpstreamCommit], submodule, "moving the submodule checkout", cancellationToken)
            .ConfigureAwait(false);

        await _fileSystem.File.WriteAllBytesAsync(SnapshotPaths.AcceptedDocument, normalizedBytes, cancellationToken)
            .ConfigureAwait(false);
        await UpdateSnapshotMarkdownAsync(receipt.UpstreamCommit, cancellationToken).ConfigureAwait(false);

        var committedReceipt = receipt with { NormalizedDocumentPath = null };
        await _fileSystem.File
            .WriteAllTextAsync(
                SnapshotPaths.CommittedReceipt,
                JsonSerializer.Serialize(committedReceipt, ToolJsonContext.Default.SnapshotReceipt) + "\n",
                cancellationToken)
            .ConfigureAwait(false);
        await RepinSourceWatchAsync(repinned, cancellationToken).ConfigureAwait(false);
        return committedReceipt;
    }

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

    private static void CompareStats(DocumentStats stats, SnapshotReceipt receipt, List<string> problems)
    {
        if (!string.Equals(stats.OperationSetDigest, receipt.OperationSetDigest, StringComparison.Ordinal))
        {
            problems.Add("the operation-set digest does not match the receipt");
        }

        if (stats.OperationIds.Count != receipt.OperationCount)
        {
            problems.Add("the operation count does not match the receipt");
        }

        if (stats.ComponentCount != receipt.ComponentCount)
        {
            problems.Add("the component count does not match the receipt");
        }

        if (stats.ContentSchemaCount != receipt.ContentSchemaCount)
        {
            problems.Add("the contentSchema count does not match the receipt");
        }
    }

    private async Task<(byte[] NormalizedBytes, string BaselineSha, IReadOnlyList<ReceiptPatch> Patches)> GenerateAsync(
        string commit, string scratchDirectory, IReadOnlyList<LoadedPatch> patches, CancellationToken cancellationToken)
    {
        var worktree = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(scratchDirectory, "worktree"));
        var submodule = _fileSystem.Path.GetFullPath(SnapshotPaths.Submodule);
        if (_fileSystem.Directory.Exists(worktree))
        {
            _ = await _processRunner.RunAsync("git", ["worktree", "remove", "--force", worktree], submodule, cancellationToken)
                .ConfigureAwait(false);
            if (_fileSystem.Directory.Exists(worktree))
            {
                throw new SnapshotRefreshException($"a stale worktree at '{worktree}' could not be removed");
            }
        }

        _ = await RunCheckedAsync(
                "git", ["worktree", "add", "--detach", worktree, commit], submodule, "adding the generation worktree", cancellationToken)
            .ConfigureAwait(false);
        try
        {
            _ = await RunCheckedAsync(
                    "bun", ["install", "--frozen-lockfile", "--ignore-scripts"], worktree, "installing upstream packages",
                    cancellationToken)
                .ConfigureAwait(false);

            var protocolDirectory = _fileSystem.Path.Combine(worktree, SnapshotPaths.UpstreamProtocolPackage);
            var artifactPath = _fileSystem.Path.Combine(worktree, SnapshotPaths.UpstreamArtifact);
            var baselineBytes = await RunGeneratorAsync(protocolDirectory, artifactPath, "baseline", cancellationToken)
                .ConfigureAwait(false);
            var baselineSha = DocumentInspector.Sha256Hex(baselineBytes);

            // The retirement verdict comes before the preimages: upstream's own fix may move or
            // delete a file a patch touches, and then the verdict is the one worth reporting.
            CheckRepairPredicates(baselineBytes, patches);
            var receiptPatches = await HashPreimagesAsync(worktree, patches, cancellationToken).ConfigureAwait(false);
            if (patches.Count is 0)
            {
                return (baselineBytes, baselineSha, receiptPatches);
            }

            foreach (var patch in patches)
            {
                var patchPath = _fileSystem.Path.GetFullPath(patch.PatchPath);
                _ = await RunCheckedAsync(
                        "git", ["apply", patchPath], worktree, $"applying patch '{patch.ManifestName}'", cancellationToken)
                    .ConfigureAwait(false);
            }

            var normalizedBytes = await RunGeneratorAsync(protocolDirectory, artifactPath, "patched", cancellationToken)
                .ConfigureAwait(false);
            CheckRepairs(normalizedBytes, patches);
            return (normalizedBytes, baselineSha, receiptPatches);
        }
        finally
        {
            // Best effort: a lingering worktree is scratch debris, never accepted state.
            _ = await _processRunner
                .RunAsync("git", ["worktree", "remove", "--force", worktree], submodule, CancellationToken.None)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// Runs the protocol package's generator and reads what it wrote. The worktree checks out
    /// upstream's committed artifact at the same path, so it is deleted first: a generator that
    /// exits cleanly without writing must refuse, never pass the committed copy through.
    /// </summary>
    private async Task<byte[]> RunGeneratorAsync(string protocolDirectory, string artifactPath, string run,
        CancellationToken cancellationToken)
    {
        _fileSystem.File.Delete(artifactPath);
        _ = await RunCheckedAsync("bun", ["run", "generate"], protocolDirectory, $"running the {run} generator", cancellationToken)
            .ConfigureAwait(false);
        if (!_fileSystem.File.Exists(artifactPath))
        {
            throw new SnapshotRefreshException(
                $"the {run} generator run exited cleanly but wrote no '{SnapshotPaths.UpstreamArtifact}'");
        }

        return await _fileSystem.File.ReadAllBytesAsync(artifactPath, cancellationToken).ConfigureAwait(false);
    }

    private async Task<IReadOnlyList<ReceiptPatch>> HashPreimagesAsync(string worktree, IReadOnlyList<LoadedPatch> patches,
        CancellationToken cancellationToken)
    {
        var receiptPatches = new List<ReceiptPatch>(patches.Count);
        foreach (var patch in patches)
        {
            var preimages = new List<ReceiptPreimage>(patch.Manifest.Touches.Count);
            foreach (var touched in patch.Manifest.Touches)
            {
                var path = _fileSystem.Path.Combine(worktree, touched);
                if (!_fileSystem.File.Exists(path))
                {
                    throw new SnapshotRefreshException(
                        $"patch '{patch.ManifestName}' touches '{touched}', which does not exist at the target commit");
                }

                var bytes = await _fileSystem.File.ReadAllBytesAsync(path, cancellationToken).ConfigureAwait(false);
                preimages.Add(new ReceiptPreimage
                {
                    Path = touched,
                    Sha256 = DocumentInspector.Sha256Hex(bytes),
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

    private async Task<PrepareOutcome> WriteScratchArtifactsAsync(PreparedCandidate candidate, CancellationToken cancellationToken)
    {
        var stats = DocumentInspector.Inspect(candidate.NormalizedBytes);
        var acceptedBytes = await _fileSystem.File.ReadAllBytesAsync(SnapshotPaths.AcceptedDocument, cancellationToken)
            .ConfigureAwait(false);
        var acceptedIds = DocumentInspector.Inspect(acceptedBytes).OperationIds.ToHashSet(StringComparer.Ordinal);
        var candidateIds = stats.OperationIds.ToHashSet(StringComparer.Ordinal);

        _ = _fileSystem.Directory.CreateDirectory(candidate.ScratchDirectory);
        var normalizedPath = _fileSystem.Path.Combine(candidate.ScratchDirectory, "openapi.json");
        await _fileSystem.File.WriteAllBytesAsync(normalizedPath, candidate.NormalizedBytes, cancellationToken).ConfigureAwait(false);

        var receipt = new SnapshotReceipt
        {
            SchemaVersion = ReceiptSchemaVersion,
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

        var receiptPath = _fileSystem.Path.Combine(candidate.ScratchDirectory, "receipt.json");
        await _fileSystem.File
            .WriteAllTextAsync(receiptPath, JsonSerializer.Serialize(receipt, ToolJsonContext.Default.SnapshotReceipt) + "\n",
                cancellationToken)
            .ConfigureAwait(false);
        return new PrepareOutcome
        {
            Receipt = receipt,
            ReceiptPath = receiptPath,
            NormalizedDocumentPath = normalizedPath,
        };
    }

    /// <summary>
    /// Observes the watched sources once and reads that observation twice: against the committed
    /// pins, which says whether upstream moved under a hand-written door, and against the
    /// accepted receipt's own section, which reproduces what prepare recorded instead of trusting
    /// it. Both readings answer a different question and both belong to verify (ADR-0020).
    /// </summary>
    private async Task<IReadOnlyList<string>> CheckWatchedSourcesAsync(
        SnapshotReceipt receipt, CancellationToken cancellationToken)
    {
        try
        {
            var watch = await _sourceWatchLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
            var observed = await _watchedSourceReader.ObserveAsync("HEAD", watch.Sources, cancellationToken).ConfigureAwait(false);
            return [.. SourceWatchVerifier.Compare(watch.Sources, observed),
                .. SourceWatchVerifier.CompareReceipt(receipt.WatchedSources, observed)];
        }
        catch (SnapshotRefreshException exception)
        {
            return [exception.Message];
        }
    }

    private async Task RepinSourceWatchAsync(SourceWatch repinned, CancellationToken cancellationToken)
    {
        if (repinned.Sources.Count is 0)
        {
            return;
        }

        var text = JsonSerializer.Serialize(repinned, ToolJsonContext.Default.SourceWatch) + "\n";
        var current = await _fileSystem.File.ReadAllTextAsync(SnapshotPaths.SourceWatch, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(current, text, StringComparison.Ordinal))
        {
            await _fileSystem.File.WriteAllTextAsync(SnapshotPaths.SourceWatch, text, cancellationToken).ConfigureAwait(false);
        }
    }

    private async Task UpdateSnapshotMarkdownAsync(string commit, CancellationToken cancellationToken)
    {
        var text = await _fileSystem.File.ReadAllTextAsync(SnapshotPaths.SnapshotMarkdown, cancellationToken).ConfigureAwait(false);
        var commitMatches = CommitRowRegex().Matches(text);
        var dateMatches = DateLineRegex().Matches(text);
        if (commitMatches.Count is not 1 || dateMatches.Count is not 1)
        {
            throw new SnapshotRefreshException(
                $"'{SnapshotPaths.SnapshotMarkdown}' no longer carries exactly one commit row and one date line; update it by hand");
        }

        text = CommitRowRegex().Replace(text, $"| Commit | `{commit}` |", 1);
        text = DateLineRegex()
            .Replace(text, $"Date: {DateTime.UtcNow.ToString("yyyy-MM-dd", CultureInfo.InvariantCulture)}", 1);
        await _fileSystem.File.WriteAllTextAsync(SnapshotPaths.SnapshotMarkdown, text, cancellationToken).ConfigureAwait(false);
    }

    private async Task<SnapshotReceipt> ReadReceiptAsync(string receiptPath, CancellationToken cancellationToken)
    {
        try
        {
            var stream = _fileSystem.File.OpenRead(receiptPath);
            await using (stream.ConfigureAwait(false))
            {
                return await JsonSerializer.DeserializeAsync(stream, ToolJsonContext.Default.SnapshotReceipt, cancellationToken)
                           .ConfigureAwait(false)
                       ?? throw new SnapshotRefreshException($"receipt '{receiptPath}' cannot be JSON null");
            }
        }
        catch (JsonException exception)
        {
            throw new SnapshotRefreshException($"receipt '{receiptPath}' is invalid: {exception.Message}", exception);
        }
    }

    private async Task<ProcessResult> RunCheckedAsync(string fileName, IReadOnlyList<string> arguments, string workingDirectory,
        string description, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync(fileName, arguments, workingDirectory, cancellationToken).ConfigureAwait(false);
        if (result.ExitCode is not 0)
        {
            var detail = result.StandardError.Trim();
            throw new SnapshotRefreshException(
                $"{description} failed ({fileName} {string.Join(' ', arguments)}, exit "
                + $"{result.ExitCode.ToString(CultureInfo.InvariantCulture)}){(detail.Length > 0 ? $": {detail}" : string.Empty)}");
        }

        return result;
    }

    [GeneratedRegex("^[0-9a-f]{40}$", RegexOptions.None, matchTimeoutMilliseconds: 1000)]
    private static partial Regex FullShaRegex();

    [GeneratedRegex(@"^\| Commit \| \S+ \|$", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex CommitRowRegex();

    [GeneratedRegex(@"^Date: \d{4}-\d{2}-\d{2}$", RegexOptions.Multiline, matchTimeoutMilliseconds: 1000)]
    private static partial Regex DateLineRegex();
}
