using Microsoft.CodeAnalysis;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// Finds a rule that one file suppresses through more than one channel. Each channel reaches files
/// its own way: a source suppression reaches its own file, a slopwatch baseline entry the file it
/// names, a project's warning list every C# file the project compiles (a <c>Directory.Build</c>
/// file's list every project below it), a global config every C# file, and the <c>.editorconfig</c>
/// files whatever Roslyn resolves for that file, so a later section that raises a rule again
/// cancels an earlier one exactly as it does in the build. A category-wide or every-analyzer
/// severity names no rule and is not attributed; a pragma without IDs names none either.
/// </summary>
internal static class SuppressionOverlap
{
    private const string DiagnosticKeyPrefix = "dotnet_diagnostic.";
    private const string SeverityKeySuffix = ".severity";
    private const string EveryRule = "*";

    public static IReadOnlyList<string> Find(SuppressionInventory inventory)
    {
        ArgumentNullException.ThrowIfNull(inventory);

        var channels = new Dictionary<(string File, string Rule), SortedSet<SuppressionChannel>>();
        foreach (var suppression in inventory.Suppressions.Where(static suppression => suppression.Rule != EveryRule))
        {
            foreach (var (file, rule) in Reach(inventory, suppression))
            {
                if (!channels.TryGetValue((file, rule), out var set))
                {
                    set = [];
                    channels.Add((file, rule), set);
                }

                set.Add(suppression.Channel);
            }
        }

        var editorConfigs = inventory.AnalyzerConfigs.Where(static config => !config.IsGlobal).ToList();
        if (editorConfigs.Count > 0)
        {
            var resolver = AnalyzerConfigSet.Create(editorConfigs.Select(static config => AnalyzerConfig.Parse(config.Text, config.FullPath)).ToList());
            foreach (var group in channels.GroupBy(static pair => pair.Key.File, StringComparer.Ordinal))
            {
                var severities = resolver.GetOptionsForSourcePath(AnalyzerConfigAudit.FullPath(inventory.Root, group.Key)).TreeOptions;
                foreach (var (key, set) in group)
                {
                    if (severities.TryGetValue(key.Rule, out var severity) && severity is ReportDiagnostic.Suppress or ReportDiagnostic.Hidden or ReportDiagnostic.Info)
                    {
                        set.Add(SuppressionChannel.EditorConfig);
                    }
                }
            }
        }

        return
        [
            .. channels
                .Where(static pair => pair.Value.Count > 1)
                .OrderBy(static pair => pair.Key.File, StringComparer.Ordinal)
                .ThenBy(static pair => pair.Key.Rule, StringComparer.Ordinal)
                .Select(static pair => $"{pair.Key.File}: {pair.Key.Rule} is suppressed through {string.Join(", ", pair.Value)}"),
        ];
    }

    private static IEnumerable<(string File, string Rule)> Reach(SuppressionInventory inventory, Suppression suppression) =>
        suppression.Channel switch
        {
            SuppressionChannel.Pragma or SuppressionChannel.SuppressMessage or SuppressionChannel.SlopwatchSuppress
                or SuppressionChannel.SlopwatchIgnore => [(suppression.Origin, suppression.Rule)],
            SuppressionChannel.SlopwatchBaseline => [(suppression.Scope, suppression.Rule)],
            SuppressionChannel.NoWarn or SuppressionChannel.WarningsNotAsErrors =>
                ProjectFiles(inventory, suppression.Origin).Select(file => (file, suppression.Rule)),
            SuppressionChannel.GlobalConfig => SeverityRule(suppression) is { } rule
                ? inventory.Files.Where(static file => file.EndsWith(".cs", StringComparison.Ordinal)).Select(file => (file, rule))
                : [],
            SuppressionChannel.EditorConfig or SuppressionChannel.BuildSwitch => [],
            _ => throw new ArgumentOutOfRangeException(nameof(suppression), suppression.Channel, "The overlap check does not know how this channel reaches files."),
        };

    private static IEnumerable<string> ProjectFiles(SuppressionInventory inventory, string origin)
    {
        var name = origin[(origin.LastIndexOf('/', StringComparison.Ordinal) + 1)..];
        if (origin.EndsWith(".csproj", StringComparison.Ordinal))
        {
            return inventory.Projects.FilesOf(origin);
        }

        if (name.StartsWith("Directory.Build.", StringComparison.Ordinal))
        {
            return inventory.Projects.FilesUnder(ProjectMembership.DirectoryOf(origin));
        }

        return origin.EndsWith(".cs", StringComparison.Ordinal)
            ? [origin]
            : throw new InvalidOperationException(
                $"{origin} sets a warning list, but only a project, a Directory.Build file or a file-based app can be attributed to the files it reaches.");
    }

    private static string? SeverityRule(Suppression suppression) =>
        suppression.Rule.StartsWith(DiagnosticKeyPrefix, StringComparison.OrdinalIgnoreCase)
        && suppression.Rule.EndsWith(SeverityKeySuffix, StringComparison.OrdinalIgnoreCase)
            ? suppression.Rule[DiagnosticKeyPrefix.Length..^SeverityKeySuffix.Length]
            : null;
}
