using OpenCode.Sdk.Tools.Generator.Refresh.Models;

namespace OpenCode.Sdk.Tools.Generator.Refresh;

/// <summary>
/// Compares the committed source-watch pins against one observation of the same sources. Every
/// disagreement is a review trigger naming the owned behavior at stake — a changed blob, a lost
/// anchor, or a source the observation never saw.
/// </summary>
internal static class SourceWatchVerifier
{
    public static IReadOnlyList<string> Compare(IReadOnlyList<WatchedSource> pinned, IReadOnlyList<ReceiptWatchedSource> observed)
    {
        ArgumentNullException.ThrowIfNull(pinned);
        ArgumentNullException.ThrowIfNull(observed);

        var byPath = observed.ToDictionary(static observation => observation.Path, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var source in pinned)
        {
            if (!byPath.TryGetValue(source.Path, out var observation))
            {
                problems.Add($"watched source '{source.Path}' was not observed");
                continue;
            }

            if (!string.Equals(observation.Sha256, source.Sha256, StringComparison.Ordinal))
            {
                problems.Add(
                    $"watched source '{source.Path}' changed: pinned {source.Sha256}, found {observation.Sha256}; "
                    + $"review the code that depends on it: {string.Join("; ", source.Behaviors.Select(static behavior => $"{behavior.Owner}: {behavior.Behavior}"))}");
            }

            foreach (var text in observation.Anchors.Where(static anchor => !anchor.Matched).Select(static anchor => anchor.Text))
            {
                var behavior = source.Behaviors.Single(behavior => string.Equals(behavior.Anchor.Text, text, StringComparison.Ordinal));
                problems.Add(
                    $"watched source '{source.Path}' no longer carries its anchor ({behavior.Anchor.Type} '{text}') for {behavior.Owner}: "
                    + behavior.Behavior);
            }
        }

        return Array.AsReadOnly([.. problems]);
    }

    /// <summary>
    /// Reproduces the receipt's <c>watchedSources</c> section from one fresh observation of the
    /// same sources. Comparing the committed pins against the checkout says whether upstream
    /// moved; this says whether the accepted receipt still describes the checkout it names, which
    /// is what "the accepted snapshot reproduces its receipt" has to mean for this section as much
    /// as for the document hash and the statistics (ADR-0020).
    /// </summary>
    /// <param name="recorded">The section the accepted receipt carries.</param>
    /// <param name="observed">The same sources observed again at the current checkout.</param>
    /// <returns>One named problem per disagreement; empty when the section reproduces.</returns>
    public static IReadOnlyList<string> CompareReceipt(
        IReadOnlyList<ReceiptWatchedSource> recorded, IReadOnlyList<ReceiptWatchedSource> observed)
    {
        ArgumentNullException.ThrowIfNull(recorded);
        ArgumentNullException.ThrowIfNull(observed);

        var byPath = observed.ToDictionary(static observation => observation.Path, StringComparer.Ordinal);
        var problems = new List<string>();
        foreach (var source in recorded)
        {
            if (!byPath.TryGetValue(source.Path, out var observation))
            {
                problems.Add($"the receipt records watched source '{source.Path}', which the source watch no longer names");
                continue;
            }

            if (!string.Equals(observation.Sha256, source.Sha256, StringComparison.Ordinal))
            {
                problems.Add(
                    $"the receipt records watched source '{source.Path}' at {source.Sha256}, "
                    + $"but this checkout carries {observation.Sha256}");
            }

            if (!observation.Anchors.SequenceEqual(source.Anchors))
            {
                problems.Add(
                    $"the receipt records watched source '{source.Path}' with different anchors than this checkout observes");
            }
        }

        var recordedPaths = recorded.Select(static source => source.Path).ToHashSet(StringComparer.Ordinal);
        foreach (var observation in observed.Where(observation => !recordedPaths.Contains(observation.Path)))
        {
            problems.Add($"the source watch names '{observation.Path}', which the receipt does not record");
        }

        return Array.AsReadOnly([.. problems]);
    }
}
