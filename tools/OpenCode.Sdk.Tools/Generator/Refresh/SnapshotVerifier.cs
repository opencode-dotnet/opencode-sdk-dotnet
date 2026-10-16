using OpenCode.Sdk.Tools.Generator.Refresh.Models;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>Compares a captured accepted state with its receipt without regenerating or writing.</summary>
internal sealed class SnapshotVerifier(PatchSetLoader patchSetLoader, WatchedSourceReader watchedSourceReader)
{
    public async Task<VerifyOutcome> VerifyAsync(SnapshotState state, CancellationToken cancellationToken)
    {
        if (state.Receipt is null)
        {
            return new VerifyOutcome
            {
                UpstreamCommit = "<none>",
                Problems = ["no committed receipt exists; the accepted snapshot predates the synchronizer"],
            };
        }

        var receipt = SnapshotStateReader.ParseReceipt(state.Receipt, SnapshotPaths.CommittedReceipt);
        var problems = new List<string>();
        var acceptedSha = DocumentInspector.Sha256Hex(state.Document);
        if (!string.Equals(acceptedSha, receipt.NormalizedDocumentSha256, StringComparison.Ordinal))
        {
            problems.Add($"accepted document hash {acceptedSha} does not match the receipt's {receipt.NormalizedDocumentSha256}");
        }

        problems.AddRange(DocumentInspector.CompareStats(DocumentInspector.Inspect(state.Document), receipt));
        if (!string.Equals(state.Checkout, receipt.UpstreamCommit, StringComparison.Ordinal))
        {
            problems.Add($"submodule checkout {state.Checkout} does not match the receipt's {receipt.UpstreamCommit}");
        }

        try
        {
            var patches = await patchSetLoader.LoadAsync(cancellationToken).ConfigureAwait(false);
            if (!patches.Select(static patch => (patch.ManifestName, patch.Manifest.Sha256))
                .SequenceEqual(receipt.Patches.Select(static patch => (patch.Manifest, patch.PatchSha256))))
            {
                problems.Add("the ordered patch set does not match the receipt's manifest names and patch hashes; re-run prepare");
            }
        }
        catch (SnapshotRefreshException exception)
        {
            problems.Add(exception.Message);
        }

        try
        {
            var watch = SourceWatchLoader.Parse(state.SourceWatch);
            // Both comparisons read one observation: pins and the receipt answer different questions.
            var observed = await watchedSourceReader.ObserveAsync(state.Checkout, watch.Sources, cancellationToken).ConfigureAwait(false);
            problems.AddRange(SourceWatchVerifier.Compare(watch.Sources, observed));
            problems.AddRange(SourceWatchVerifier.CompareReceipt(receipt.WatchedSources, observed));
        }
        catch (SnapshotRefreshException exception)
        {
            problems.Add(exception.Message);
        }

        return new VerifyOutcome { UpstreamCommit = receipt.UpstreamCommit, Problems = problems };
    }
}
