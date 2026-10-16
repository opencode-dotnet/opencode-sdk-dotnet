using System.Text;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.Suppressions.Support;

/// <summary>
/// A small repository on the filesystem double, scanned the way the ledger scans the real one.
/// Projects, the root <c>.editorconfig</c> and slopwatch baseline entries have their own verbs;
/// a file whose exact spelling is the subject of a test goes in through <see cref="WithFile"/>.
/// </summary>
internal sealed class SuppressionScenario
{
    private const string EditorConfigPath = ".editorconfig";

    private readonly MockFileSystem _fileSystem = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private readonly HashSet<string> _ignored = new(StringComparer.Ordinal);
    private readonly List<string> _baselineEntries = [];

    /// <summary>Adds one repository file from its lines, joined with line feeds as the repository stores them.</summary>
    public SuppressionScenario WithFile(string path, params string[] lines)
    {
        _files[path] = string.Join('\n', lines) + "\n";
        return this;
    }

    public SuppressionScenario WithIgnoredFile(string path, params string[] lines)
    {
        _ignored.Add(path);
        return WithFile(path, lines);
    }

    /// <summary>Adds an SDK-style project, optionally with a <c>NoWarn</c> property and one linked <c>Compile</c> item.</summary>
    public SuppressionScenario WithProject(string path, string? noWarn = null, string? compileInclude = null)
    {
        var project = new StringBuilder("<Project Sdk=\"Microsoft.NET.Sdk\">\n");
        if (noWarn is not null)
        {
            project.Append("  <PropertyGroup>\n    <NoWarn>$(NoWarn);").Append(noWarn).Append("</NoWarn>\n  </PropertyGroup>\n");
        }

        if (compileInclude is not null)
        {
            project.Append("  <ItemGroup>\n    <Compile Include=\"").Append(compileInclude).Append("\" />\n  </ItemGroup>\n");
        }

        return WithFile(path, project.Append("</Project>").ToString());
    }

    /// <summary>Writes the root <c>.editorconfig</c>: <c>root = true</c>, then the given lines.</summary>
    public SuppressionScenario WithEditorConfig(params string[] lines) => WithFile(EditorConfigPath, ["root = true", .. lines]);

    /// <summary>Adds one entry to <c>.slopwatch/baseline.json</c>, in the shape Slopwatch writes.</summary>
    public SuppressionScenario WithBaselineEntry(string rule, string file, string hash)
    {
        _baselineEntries.Add(
            $"{{ \"ruleId\": \"{rule}\", \"filePath\": \"{file}\", \"lineNumber\": 1, \"codeSnippet\": \"catch {{ }}\", \"hash\": \"{hash}\" }}");
        return WithFile(SuppressionScan.SlopwatchBaselinePath, $"{{ \"version\": 1, \"entries\": [ {string.Join(", ", _baselineEntries)} ] }}");
    }

    public async Task<SuppressionInventory> ScanAsync()
    {
        var root = _fileSystem.Path.GetFullPath("repo");
        _fileSystem.Directory.CreateDirectory(root);
        foreach (var (path, text) in _files)
        {
            var fullPath = _fileSystem.Path.Combine(root, path);
            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(fullPath)!);
            await _fileSystem.File.WriteAllTextAsync(fullPath, text);
        }

        return await new SuppressionScan(_fileSystem, [.. _files.Keys.Except(_ignored, StringComparer.Ordinal)]).ReadAsync(root, CancellationToken.None);
    }

    /// <summary>The ledger lines of one scan, without the header and the totals.</summary>
    public async Task<IReadOnlyList<string>> LedgerLinesAsync()
    {
        var lines = SuppressionLedger.Render((await ScanAsync()).Suppressions).Split('\n');
        return
        [
            .. lines
                .SkipWhile(static line => line.Length > 0).Skip(1)
                .SkipWhile(static line => line.Length > 0).Skip(1)
                .Where(static line => line.Length > 0),
        ];
    }
}
