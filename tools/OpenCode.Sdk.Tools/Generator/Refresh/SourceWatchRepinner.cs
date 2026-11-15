using OpenCode.Sdk.Tools.Generator.Refresh.Models;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>
/// The apply-time source-watch rule: a reviewed receipt re-pins the committed watch to the
/// hashes it observed. The watch and the receipt must describe the same source set, and an
/// anchor the receipt saw fail is never re-pinned silently — its owner's behavior moved, so a
/// human reads that product's code and rewrites the anchor before the pin advances.
/// </summary>
internal static class SourceWatchRepinner
{
    public static SourceWatch Repin(SourceWatch pinned, IReadOnlyList<ReceiptWatchedSource> observed)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(observed);

        var byPath = observed.ToDictionary(static observation => observation.Path, StringComparer.Ordinal);
        if (byPath.Count != pinned.Sources.Count || pinned.Sources.Any(source => !byPath.ContainsKey(source.Path)))
        {
            throw new SnapshotRefreshException(
                "the source watch names different files than the receipt observed; re-run prepare");
        }

        var drift = pinned.Sources.FirstOrDefault(source => !byPath[source.Path].Anchors.Select(static anchor => anchor.Text)
            .SequenceEqual(source.Behaviors.Select(static behavior => behavior.Anchor.Text), StringComparer.Ordinal));
        if (drift is not null)
        {
            throw new SnapshotRefreshException($"the receipt's anchor texts for '{drift.Path}' differ from the source watch; re-run prepare");
        }

        var unmatched = pinned.Sources.SelectMany(source => source.Behaviors
                .Where((_, index) => !byPath[source.Path].Anchors[index].Matched)
                .Select(behavior => $"{source.Path} ('{behavior.Anchor.Text}', {behavior.Owner})"))
            .ToArray();
        if (unmatched.Length > 0)
        {
            throw new SnapshotRefreshException(
                $"the receipt reports lost anchors in {string.Join(", ", unmatched)}; read the owning products' code, "
                + "rewrite those anchors, then re-run prepare");
        }

        return pinned with { Sources = [.. pinned.Sources.Select(source => source with { Sha256 = byPath[source.Path].Sha256 })] };
    }
}
