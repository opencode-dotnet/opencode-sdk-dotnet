using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// Checks the shape of the repository's <c>.editorconfig</c> files: a section glob is declared once
/// per file, and a section that names files still matches one. Two kinds of section name files: a
/// glob with a <c>/</c>, which is anchored to the config's directory and names a place in the tree,
/// and a glob without wildcards, which names one file by its name. Each of their brace alternatives
/// must match at least one repository file, probed with the anchoring the whole glob has. Any other
/// glob names a kind of file, which may have no instance today. Matching runs through Roslyn's own
/// analyzer-config matcher, so the audit reads globs as the build does. Global configs carry no
/// section globs here and are not audited.
/// </summary>
internal static class AnalyzerConfigAudit
{
    private const string ProbeKeyPrefix = "suppression_ledger_probe_";
    private static readonly char[] Wildcards = ['*', '?', '[', '{'];

    public static IReadOnlyList<string> FindDuplicateSections(IEnumerable<AnalyzerConfigDocument> configs) =>
    [
        .. configs.SelectMany(static config => config.Sections
            .GroupBy(static section => section.Name, StringComparer.Ordinal)
            .Where(static group => group.Count() > 1)
            .Select(group => $"{config.Origin}: [{group.Key}] at lines {string.Join(", ", group.Select(static section => Number(section.Line)))}")),
    ];

    public static IReadOnlyList<string> FindUnmatchedSections(SuppressionInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var result = new List<string>();
        foreach (var config in inventory.AnalyzerConfigs.Where(static config => !config.IsGlobal))
        {
            var probes = config.Sections
                .Where(static section => section.Name.Contains('/', StringComparison.Ordinal) || section.Name.IndexOfAny(Wildcards) < 0)
                .SelectMany(static section => ExpandAlternatives(section.Name).Select(alternative => (Section: section, Alternative: alternative)))
                .ToList();
            if (probes.Count == 0)
            {
                continue;
            }

            var text = new StringBuilder();
            for (var index = 0; index < probes.Count; index++)
            {
                text.Append('[').Append(Anchored(probes[index].Section.Name, probes[index].Alternative)).Append("]\n")
                    .Append(ProbeKey(index)).Append(" = true\n");
            }

            var matcher = AnalyzerConfigSet.Create(new[] { AnalyzerConfig.Parse(text.ToString(), config.FullPath) });
            var matched = new HashSet<string>(StringComparer.Ordinal);
            foreach (var file in inventory.Files)
            {
                matched.UnionWith(matcher.GetOptionsForSourcePath(FullPath(inventory.Root, file)).AnalyzerOptions.Keys);
            }

            result.AddRange(probes
                .Where((_, index) => !matched.Contains(ProbeKey(index)))
                .Select(probe => string.Equals(probe.Alternative, probe.Section.Name, StringComparison.Ordinal)
                    ? $"{config.Origin}:{Number(probe.Section.Line)}: [{probe.Section.Name}] matches no file"
                    : $"{config.Origin}:{Number(probe.Section.Line)}: [{probe.Section.Name}] has an alternative, '{probe.Alternative}', that matches no file"));
        }

        return result;
    }

    /// <summary>A repository file's full path in the form Roslyn's analyzer-config matcher compares.</summary>
    internal static string FullPath(string root, string file) => root.TrimEnd('/', '\\') + "/" + file;

    /// <summary>
    /// Every glob a brace group with comma-separated alternatives stands for, nested groups included,
    /// also inside a group that has a single alternative. A brace group without a comma at any level,
    /// such as a numeric range, stays as written, and so does a character class, braces included.
    /// </summary>
    internal static IReadOnlyList<string> ExpandAlternatives(string glob)
    {
        ArgumentNullException.ThrowIfNull(glob);

        if (FirstGroup(glob) is not { } group)
        {
            return [glob];
        }

        var (open, close) = group;
        var prefix = glob[..open];
        var suffix = glob[(close + 1)..];
        var alternatives = SplitTopLevel(glob[(open + 1)..close]);
        if (alternatives.Count > 1)
        {
            return [.. alternatives.SelectMany(alternative => ExpandAlternatives(prefix + alternative + suffix))];
        }

        var inner = ExpandAlternatives(alternatives[0]);
        var rest = ExpandAlternatives(suffix);
        return [.. inner.SelectMany(body => rest.Select(tail => prefix + "{" + body + "}" + tail))];
    }

    /// <summary>
    /// The alternative as the whole glob anchors it: an alternative without a <c>/</c> taken from a
    /// glob with one is anchored to the config's directory too, so the probe keeps that anchoring.
    /// </summary>
    private static string Anchored(string glob, string alternative) =>
        glob.Contains('/', StringComparison.Ordinal) && !alternative.Contains('/', StringComparison.Ordinal) ? "/" + alternative : alternative;

    /// <summary>The first top-level brace group outside a character class, by the indices of its braces.</summary>
    private static (int Open, int Close)? FirstGroup(string glob)
    {
        var depth = 0;
        var open = -1;
        var index = 0;
        while (index < glob.Length)
        {
            switch (glob[index])
            {
                case '\\':
                    index++;
                    break;
                case '[':
                    index = ClassEnd(glob, index);
                    break;
                case '{':
                    if (depth++ == 0)
                    {
                        open = index;
                    }

                    break;
                case '}' when depth > 0 && --depth == 0:
                    return (open, index);
            }

            index++;
        }

        return null;
    }

    private static List<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        var index = 0;
        while (index < body.Length)
        {
            switch (body[index])
            {
                case '\\':
                    index++;
                    break;
                case '[':
                    index = ClassEnd(body, index);
                    break;
                case '{':
                    depth++;
                    break;
                case '}':
                    depth--;
                    break;
                case ',' when depth == 0:
                    parts.Add(body[start..index]);
                    start = index + 1;
                    break;
            }

            index++;
        }

        parts.Add(body[start..]);
        return parts;
    }

    /// <summary>The index of the bracket that closes the character class opening at <paramref name="open"/>, or the opening bracket when none does.</summary>
    private static int ClassEnd(string glob, int open)
    {
        var close = glob.AsSpan(open + 1).IndexOf(']');
        return close < 0 ? open : open + 1 + close;
    }

    private static string ProbeKey(int index) => ProbeKeyPrefix + Number(index);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);
}
