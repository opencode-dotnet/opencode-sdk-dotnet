using System.Globalization;
using System.Text.Json;
using OpenCode.Sdk.Tools.Clones;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.Clones.Support;

/// <summary>
/// A repository on the filesystem double for the clone gate: the solution file, the installed
/// detector, the generator's manifest with its generated and hand-written sources, the baselines,
/// and one scripted detector run per scope that writes the report jscpd would write.
/// </summary>
internal sealed class CloneScenario
{
    internal const string ManifestPath = "src/OpenCode.Sdk/.generated-manifest.json";

    private readonly List<string> _generated = [];
    private readonly List<string> _handWritten = [];
    private bool _solution = true;
    private bool _detector = true;
    private bool _manifest = true;
    private bool _baselines = true;

    public CloneScenario()
    {
        Root = FileSystem.Directory.GetCurrentDirectory();
    }

    public MockFileSystem FileSystem { get; } = new();

    public ScriptedProcessRunner Runner { get; } = new();

    public string Root { get; }

    public CloneScenario WithGenerated(string pathUnderSdk)
    {
        _generated.Add(pathUnderSdk);
        return this;
    }

    public CloneScenario WithHandWritten(string path)
    {
        _handWritten.Add(path);
        return this;
    }

    public CloneScenario WithoutSolution()
    {
        _solution = false;
        return this;
    }

    public CloneScenario WithoutDetector()
    {
        _detector = false;
        return this;
    }

    public CloneScenario WithoutManifest()
    {
        _manifest = false;
        return this;
    }

    public CloneScenario WithoutBaselines()
    {
        _baselines = false;
        return this;
    }

    /// <summary>Scripts the next detector run: its exit code and standard error, and the report totals it writes, unless it writes none.</summary>
    public CloneScenario WithRun(string scope, int exitCode = 0, int clones = 0, int newClones = 0, string standardError = "", bool writesReport = true)
    {
        var result = new ProcessResult { ExitCode = exitCode, StandardOutput = [], StandardError = standardError };
        Runner.Expect("bun", CloneDetector.RunnerPath, result, writesReport ? () => WriteReportAsync(scope, clones, newClones) : null);
        return this;
    }

    /// <summary>Scripts a passing run for every scope, in the gate's order.</summary>
    public CloneScenario WithPassingRuns()
    {
        foreach (var scope in CloneScopes.All)
        {
            WithRun(scope.Name);
        }

        return this;
    }

    public async Task<CloneScenario> BuildAsync()
    {
        if (_solution)
        {
            await WriteAsync("OpenCode.slnx", "<Solution />");
        }

        if (_detector)
        {
            await WriteAsync(CloneDetector.RunnerPath, "// detector");
        }

        if (_manifest)
        {
            await WriteAsync(ManifestPath, JsonSerializer.Serialize(new { files = _generated }));
        }

        foreach (var path in _generated.Select(static path => "src/OpenCode.Sdk/" + path).Concat(_handWritten))
        {
            await WriteAsync(path, "internal sealed class C;");
        }

        if (_baselines)
        {
            foreach (var scope in CloneScopes.All.Where(static scope => scope.Baseline is not null))
            {
                await WriteAsync(scope.Baseline!, "{ \"version\": 1, \"fingerprints\": {} }");
            }
        }

        return this;
    }

    /// <summary>The folders the command created for its configs and reports and has not removed.</summary>
    public IReadOnlyList<string> WorkFolders() =>
    [
        .. FileSystem.Directory.EnumerateDirectories(FileSystem.Path.GetTempPath(), "opencode-clones-*"),
    ];

    private Task WriteAsync(string path, string text)
    {
        var fullPath = FileSystem.Path.Combine(Root, path);
        FileSystem.Directory.CreateDirectory(FileSystem.Path.GetDirectoryName(fullPath)!);
        return FileSystem.File.WriteAllTextAsync(fullPath, text);
    }

    /// <summary>
    /// One duplicate as jscpd reports it: two fragments named relative to the scanned path (with the
    /// platform's separator, as jscpd writes them on Windows), its token count, and whether the
    /// baseline holds it.
    /// </summary>
    internal static string Duplicate(string first, string second, bool isNew) =>
        $$"""{ "firstFile": { "name": "Deep\\{{first}}", "start": 3, "end": 33 }, "secondFile": { "name": "Deep\\{{second}}", "start": 5, "end": 35 }, "tokens": 212, "lines": 31, "isNew": {{(isNew ? "true" : "false")}} }""";

    /// <summary>Writes the report the detector would write, into the output folder named by the scope's config.</summary>
    private async Task WriteReportAsync(string scope, int clones, int newClones)
    {
        var config = FileSystem.Directory
            .EnumerateFiles(FileSystem.Path.GetTempPath(), scope + ".json", SearchOption.AllDirectories)
            .Single();
        using var document = JsonDocument.Parse(await FileSystem.File.ReadAllTextAsync(config));
        var output = document.RootElement.GetProperty("output").GetString()!;
        FileSystem.Directory.CreateDirectory(output);
        var duplicates = Enumerable.Range(0, newClones)
            .Select(static index => index.ToString(CultureInfo.InvariantCulture))
            .Select(static index => Duplicate("A" + index + ".cs", "B" + index + ".cs", isNew: true))
            .Append(Duplicate("Old.cs", "Older.cs", isNew: false));
        var report = string.Create(
            CultureInfo.InvariantCulture,
            $$"""{ "duplicates": [ {{string.Join(", ", duplicates)}} ], "statistics": { "total": { "clones": {{clones}}, "duplicatedLines": {{clones * 10}}, "lines": 1000, "percentage": {{clones}}, "newClones": {{newClones}} } } }""");
        await FileSystem.File.WriteAllTextAsync(FileSystem.Path.Combine(output, CloneDetector.ReportFileName), report);
    }
}
