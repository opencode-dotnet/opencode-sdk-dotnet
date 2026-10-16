using System.Globalization;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

internal static class SiteList
{
    public static IReadOnlyList<SiteListEntry> Parse(string list, string text)
    {
        var entries = new List<SiteListEntry>();
        var rows = text.Split('\n');
        for (var index = 0; index < rows.Length; index++)
        {
            if (rows[index].Length == 0)
            {
                continue;
            }

            var fields = rows[index].Split('\t');
            if (fields.Length != 2 || !fields[0].StartsWith("tests/", StringComparison.Ordinal)
                || !fields[0].EndsWith(".cs", StringComparison.Ordinal) || fields[0].IndexOfAny(['\\', ':']) >= 0 || fields[0].Any(char.IsControl)
                || fields[0].Split('/').Any(static part => part is "" or "." or "..")
                || !int.TryParse(fields[1], NumberStyles.None, CultureInfo.InvariantCulture, out var count) || count <= 0
                || (entries.Count > 0 && StringComparer.Ordinal.Compare(entries[^1].File, fields[0]) >= 0))
            {
                throw new InvalidOperationException(string.Create(CultureInfo.InvariantCulture,
                    $"{list}:{index + 1}: expected an ordinally sorted, unique tests/*.cs path and a positive count separated by TAB."));
            }

            entries.Add(new SiteListEntry(fields[0], count, index + 1));
        }

        return entries;
    }

    public static IReadOnlyList<string> Compare(string list, IReadOnlyList<SiteListEntry> entries, IReadOnlyList<RepositoryGateSite> sites, string rule)
    {
        var messages = new List<string>();
        var actual = sites.ToLookup(static site => site.File, StringComparer.Ordinal);
        foreach (var path in entries.Select(static entry => entry.File).Union(actual.Select(static group => group.Key), StringComparer.Ordinal).Order(StringComparer.Ordinal))
        {
            var entry = entries.SingleOrDefault(entry => string.Equals(entry.File, path, StringComparison.Ordinal));
            var count = actual[path].Count();
            if (count > (entry?.Count ?? 0))
            {
                var candidates = string.Join('\n', actual[path].OrderBy(static site => site.Line).ThenBy(static site => site.Span));
                messages.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{list}:{entry?.Line ?? 1}: {rule}: growth in {path}: listed {entry?.Count ?? 0}, found {count}.\nCurrent candidate sites (the count baseline cannot identify which was added):\n{candidates}\nDo not grow the list without the named PR reason; fix the added site."));
            }
            else if (entry is not null && count < entry.Count)
            {
                var instruction = count == 0
                    ? string.Create(CultureInfo.InvariantCulture, $"delete '{path}\t{entry.Count}' (no remaining sites or file disappeared).")
                    : string.Create(CultureInfo.InvariantCulture, $"replace '{path}\t{entry.Count}' with '{path}\t{count}'.");
                messages.Add(string.Create(CultureInfo.InvariantCulture,
                    $"{list}:{entry.Line}: {rule}: found {count}, listed {entry.Count}; shrink the list: {instruction}"));
            }
        }

        return messages;
    }
}
