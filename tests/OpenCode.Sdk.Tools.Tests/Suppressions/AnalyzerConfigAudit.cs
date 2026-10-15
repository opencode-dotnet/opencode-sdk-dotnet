using System.Globalization;
using System.Text;
using Microsoft.CodeAnalysis;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// Checks the shape of the repository's <c>.editorconfig</c> files: a section glob is declared once
/// per file, and a section scoped to a path still matches a file. A glob with a <c>/</c> is anchored
/// to the config's directory and names a place in the tree, so each of its brace alternatives must
/// match at least one repository file; a glob without one matches file names anywhere and names a
/// kind of file, which may have no instance today. Matching runs through Roslyn's own analyzer-config
/// matcher, so the audit reads globs exactly as the build does. Global configs carry no section globs
/// here and are not audited.
/// </summary>
internal static class AnalyzerConfigAudit
{
    private const string ProbeKeyPrefix = "suppression_ledger_probe_";

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
                .Where(static section => section.Name.Contains('/', StringComparison.Ordinal))
                .SelectMany(static section => ExpandAlternatives(section.Name).Select(alternative => (Section: section, Alternative: alternative)))
                .ToList();
            if (probes.Count == 0)
            {
                continue;
            }

            var text = new StringBuilder();
            for (var index = 0; index < probes.Count; index++)
            {
                text.Append('[').Append(probes[index].Alternative).Append("]\n").Append(ProbeKey(index)).Append(" = true\n");
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

    private static string ProbeKey(int index) => ProbeKeyPrefix + Number(index);

    private static string Number(int value) => value.ToString(CultureInfo.InvariantCulture);

    /// <summary>A repository file's full path in the form Roslyn's analyzer-config matcher compares.</summary>
    internal static string FullPath(string root, string file) => root.TrimEnd('/', '\\') + "/" + file;

    /// <summary>
    /// Every glob a brace group with comma-separated alternatives stands for, nested groups included.
    /// A brace group without a top-level comma, such as a numeric range, stays as written.
    /// </summary>
    internal static IReadOnlyList<string> ExpandAlternatives(string glob)
    {
        var depth = 0;
        var open = -1;
        var escaped = false;
        for (var index = 0; index < glob.Length; index++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            switch (glob[index])
            {
                case '\\':
                    escaped = true;
                    break;
                case '{':
                    if (depth++ == 0)
                    {
                        open = index;
                    }

                    break;
                case '}' when depth > 0 && --depth == 0:
                    var alternatives = SplitTopLevel(glob[(open + 1)..index]);
                    if (alternatives.Count > 1)
                    {
                        var prefix = glob[..open];
                        var suffix = glob[(index + 1)..];
                        return [.. alternatives.SelectMany(alternative => ExpandAlternatives(prefix + alternative + suffix))];
                    }

                    break;
            }
        }

        return [glob];
    }

    private static List<string> SplitTopLevel(string body)
    {
        var parts = new List<string>();
        var depth = 0;
        var start = 0;
        var escaped = false;
        for (var index = 0; index < body.Length; index++)
        {
            if (escaped)
            {
                escaped = false;
                continue;
            }

            switch (body[index])
            {
                case '\\':
                    escaped = true;
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
        }

        parts.Add(body[start..]);
        return parts;
    }
}
