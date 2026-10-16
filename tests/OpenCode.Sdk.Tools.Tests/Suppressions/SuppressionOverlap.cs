using Microsoft.CodeAnalysis;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// Finds a rule that one file suppresses through more than one channel. Each channel reaches files
/// its own way:
/// <list type="bullet">
/// <item>a source suppression reaches its own file, and a slopwatch baseline entry the file it names;</item>
/// <item>a project's warning list reaches every C# file the project can compile
/// (<see cref="ProjectMembership"/>); a file-based app's reaches the app; any other MSBuild file's
/// or command line's reaches every C# file below that file's folder, the whole repository for a
/// workflow;</item>
/// <item>the <c>.editorconfig</c> files reach whatever Roslyn resolves for the file, so a later
/// section that raises a rule again cancels an earlier one, as in the build;</item>
/// <item>a global config reaches every C# file, except where an <c>.editorconfig</c> raises the rule,
/// because the file-level setting wins.</item>
/// </list>
/// Every reach errs wide: a wide reach can only report an overlap that is not there, loudly, and
/// never hides one. A category-wide or every-analyzer severity names no rule and is not attributed;
/// a pragma without IDs names none either.
/// </summary>
internal static class SuppressionOverlap
{
    private const string DiagnosticKeyPrefix = "dotnet_diagnostic.";
    private const string SeverityKeySuffix = ".severity";
    private const string EveryRule = "*";
    private const string DirectoryBuildPrefix = "Directory.Build.";
    private const string WorkflowFolder = ".github/";

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
                    if (!severities.TryGetValue(key.Rule, out var severity))
                    {
                        continue;
                    }

                    if (severity is ReportDiagnostic.Suppress or ReportDiagnostic.Hidden or ReportDiagnostic.Info)
                    {
                        set.Add(SuppressionChannel.EditorConfig);
                    }
                    else
                    {
                        set.Remove(SuppressionChannel.GlobalConfig);
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
            SuppressionChannel.NoWarn or SuppressionChannel.WarningsNotAsErrors or SuppressionChannel.MSBuildWarningsAsMessages =>
                WarningListFiles(inventory, suppression.Origin).Select(file => (file, suppression.Rule)),
            SuppressionChannel.GlobalConfig => SeverityRule(suppression) is { } rule
                ? inventory.Projects.FilesUnder(string.Empty).Select(file => (file, rule))
                : [],
            SuppressionChannel.EditorConfig or SuppressionChannel.BuildSwitch or SuppressionChannel.NullableDirective
                or SuppressionChannel.GeneratedCodeHeader or SuppressionChannel.GeneratedCodeAttribute => [],
            _ => throw new ArgumentOutOfRangeException(nameof(suppression), suppression.Channel, "The overlap check does not know how this channel reaches files."),
        };

    private static IEnumerable<string> WarningListFiles(SuppressionInventory inventory, string origin)
    {
        if (origin.EndsWith(".csproj", StringComparison.Ordinal))
        {
            return inventory.Projects.FilesOf(origin);
        }

        if (origin.EndsWith(".cs", StringComparison.Ordinal))
        {
            return [origin];
        }

        var name = origin[(origin.LastIndexOf('/', StringComparison.Ordinal) + 1)..];
        return name.StartsWith(DirectoryBuildPrefix, StringComparison.Ordinal) && !origin.StartsWith(WorkflowFolder, StringComparison.Ordinal)
            ? inventory.Projects.FilesUnder(ProjectMembership.DirectoryOf(origin))
            : inventory.Projects.FilesUnder(string.Empty);
    }

    private static string? SeverityRule(Suppression suppression) =>
        suppression.Rule.StartsWith(DiagnosticKeyPrefix, StringComparison.OrdinalIgnoreCase)
        && suppression.Rule.EndsWith(SeverityKeySuffix, StringComparison.OrdinalIgnoreCase)
            ? DiagnosticId.Normalize(suppression.Rule[DiagnosticKeyPrefix.Length..^SeverityKeySuffix.Length])
            : null;
}
