using System.Globalization;
using System.Text.RegularExpressions;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// Reads an analyzer config (an <c>.editorconfig</c> or a global config) the way Roslyn does: any
/// line break ends a line, <c>#</c> and <c>;</c> start comments, a bracketed line opens a section, a
/// <c>key = value</c> line belongs to the section above it, and an <c>is_global</c> key in the
/// preamble makes the file global whatever its value. Every entry that can lower a diagnostic
/// becomes a <see cref="Suppression"/>: a severity of none, silent, refactoring or suggestion (for
/// one rule, one category, or every analyzer), a code-style option whose severity suffix is one of those,
/// <c>generated_code = true</c>, Roslynator off by default, and every rule option (a key that starts
/// with a rule ID or with <c>dotnet_code_quality</c>), because a loosened threshold lowers a rule as
/// surely as a severity does. A line that is none of these is refused rather than skipped.
/// </summary>
internal static partial class AnalyzerConfigReader
{
    private const string GlobalConfigFileName = ".globalconfig";
    private static readonly string[] LowSeverities = ["none", "silent", "refactoring", "suggestion"];

    public static AnalyzerConfigDocument Parse(string origin, string fullPath, string text)
    {
        var preamble = new List<AnalyzerConfigEntry>();
        var sections = new List<AnalyzerConfigSection>();
        List<AnalyzerConfigEntry>? entries = null;
        var lines = text.ReplaceLineEndings("\n").Split('\n');
        for (var index = 0; index < lines.Length; index++)
        {
            var line = lines[index].Trim();
            if (line.Length == 0 || line[0] is '#' or ';')
            {
                continue;
            }

            var section = SectionLine().Match(line);
            if (section.Success)
            {
                entries = [];
                sections.Add(new AnalyzerConfigSection(section.Groups["name"].Value, index + 1, entries));
                continue;
            }

            var property = PropertyLine().Match(line);
            if (!property.Success)
            {
                throw new InvalidOperationException(string.Create(
                    CultureInfo.InvariantCulture, $"{origin}:{index + 1} is not a section, a property or a comment: '{line}'."));
            }

            (entries ?? preamble).Add(new AnalyzerConfigEntry(property.Groups["key"].Value, property.Groups["value"].Value, index + 1));
        }

        var isGlobal = origin.EndsWith(GlobalConfigFileName, StringComparison.OrdinalIgnoreCase)
            || preamble.Any(static entry => Is(entry.Key, "is_global"));
        return new AnalyzerConfigDocument(origin, fullPath, text, isGlobal, preamble, sections);
    }

    public static IEnumerable<Suppression> Suppressions(AnalyzerConfigDocument document)
    {
        ArgumentNullException.ThrowIfNull(document);

        var channel = document.IsGlobal ? SuppressionChannel.GlobalConfig : SuppressionChannel.EditorConfig;
        return document.Preamble.Select(static entry => (Section: string.Empty, Entry: entry))
            .Concat(document.Sections.SelectMany(static section => section.Entries.Select(entry => (Section: section.Name, Entry: entry))))
            .Where(static pair => Lowers(pair.Entry.Key, pair.Entry.Value))
            .Select(pair => new Suppression(channel, document.Origin, pair.Section, pair.Entry.Key, pair.Entry.Value));
    }

    private static bool Lowers(string key, string value)
    {
        if (key.EndsWith(".severity", StringComparison.OrdinalIgnoreCase))
        {
            return IsLowSeverity(value);
        }

        if (Is(key, "generated_code"))
        {
            return Is(value, "true");
        }

        if (Is(key, "roslynator_analyzers.enabled_by_default"))
        {
            return Is(value, "false");
        }

        if (RuleOption().IsMatch(key))
        {
            return true;
        }

        var suffix = value.LastIndexOf(':', StringComparison.Ordinal);
        return suffix >= 0 && IsLowSeverity(value[(suffix + 1)..]);
    }

    private static bool IsLowSeverity(string value) =>
        LowSeverities.Any(severity => Is(value.Trim(), severity));

    private static bool Is(string actual, string expected) => string.Equals(actual, expected, StringComparison.OrdinalIgnoreCase);

    [GeneratedRegex(@"^\[(?<name>(?:[^#;]|\\#|\\;)+)\]\s*(?:[#;].*)?$", RegexOptions.None, Timeout.Infinite)]
    private static partial Regex SectionLine();

    // Quadratic, so it keeps a finite match timeout: the lazy value and the trailing whitespace run overlap, so a long run of spaces rescans.
    [GeneratedRegex(@"^(?<key>[\w.\-]+)\s*[=:]\s*(?<value>.*?)\s*(?:[#;].*)?$", RegexOptions.None, 1000)]
    private static partial Regex PropertyLine();

    [GeneratedRegex(@"^(?:[A-Za-z]{1,8}[0-9]{3,5}|dotnet_code_quality)[._]", RegexOptions.IgnoreCase, Timeout.Infinite)]
    private static partial Regex RuleOption();
}
