using System.Globalization;
using System.IO.Abstractions;
using System.Text;
using System.Text.Json;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Generator.Refresh.Models;
using OpenCode.Sdk.Tools.Serialization;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>Finishes every validation and serialization before handing accepted mutations to the committer.</summary>
internal sealed class SnapshotApplyPlanner(IFileSystem fileSystem, IProcessRunner processRunner, TimeProvider timeProvider)
{
    public async Task<SnapshotApplyPlan> PlanAsync(SnapshotState state, SnapshotReceipt receipt, CancellationToken cancellationToken)
    {
        if (receipt.SchemaVersion is not SnapshotReceipt.CurrentSchemaVersion)
        {
            throw new SnapshotRefreshException($"receipt schema version {receipt.SchemaVersion.ToString(CultureInfo.InvariantCulture)} is not supported");
        }

        if (!SnapshotStateReader.IsFullCommit(receipt.UpstreamCommit))
        {
            throw new SnapshotRefreshException($"receipt upstream commit '{receipt.UpstreamCommit}' is not a full SHA");
        }

        if (string.IsNullOrWhiteSpace(receipt.ReleaseTag))
        {
            throw new SnapshotRefreshException("the receipt carries no releaseTag; re-run prepare at a named release tag");
        }

        if (string.IsNullOrEmpty(receipt.GeneratedBaselineSha256))
        {
            throw new SnapshotRefreshException("the receipt records no generator baseline, so its document may be upstream's committed copy; re-run prepare");
        }

        if (receipt.NormalizedDocumentPath is null || !fileSystem.File.Exists(receipt.NormalizedDocumentPath))
        {
            throw new SnapshotRefreshException("the receipt carries no prepared document; re-run prepare");
        }

        var normalizedBytes = await fileSystem.File.ReadAllBytesAsync(receipt.NormalizedDocumentPath, cancellationToken).ConfigureAwait(false);
        if (!string.Equals(DocumentInspector.Sha256Hex(normalizedBytes), receipt.NormalizedDocumentSha256, StringComparison.Ordinal))
        {
            throw new SnapshotRefreshException("the prepared document changed after the receipt was written (time-of-check/time-of-use drift); re-run prepare");
        }

        var driftProblems = DocumentInspector.CompareStats(DocumentInspector.Inspect(normalizedBytes), receipt);
        if (driftProblems.Count > 0)
        {
            throw new SnapshotRefreshException($"the prepared document disagrees with its receipt: {string.Join("; ", driftProblems)}");
        }

        var watch = SourceWatchLoader.Parse(state.SourceWatch);
        var repinned = SourceWatchRepinner.Repin(watch, receipt.WatchedSources);
        var submodule = fileSystem.Path.GetFullPath(SnapshotPaths.Submodule);
        _ = await processRunner.RunCheckedAsync("git", ["rev-parse", "--verify", $"{receipt.UpstreamCommit}^{{commit}}"], submodule,
            "verifying the receipt commit exists", cancellationToken).ConfigureAwait(false);

        var markdown = SnapshotMarkdownRenderer.Render(Encoding.UTF8.GetString(state.Markdown ?? []), receipt,
            DateOnly.FromDateTime(timeProvider.GetUtcNow().UtcDateTime));
        string? watchText = null;
        if (!repinned.Sources.SequenceEqual(watch.Sources))
        {
            watchText = JsonSerializer.Serialize(repinned, ToolJsonContext.Default.SourceWatch) + "\n";
        }

        var committedReceipt = receipt with { NormalizedDocumentPath = null };
        return new SnapshotApplyPlan(committedReceipt, normalizedBytes, markdown, watchText,
            SnapshotStateReader.SerializeReceipt(committedReceipt));
    }
}
