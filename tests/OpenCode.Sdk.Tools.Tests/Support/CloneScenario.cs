using System.Globalization;
using System.Text.Json;
using OpenCode.Sdk.Tools.Clones;
using OpenCode.Sdk.Tools.Generator.Refresh;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using Testably.Abstractions;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.Support;

/// <summary>
/// A repository on the filesystem double for the clone gate: the solution file, the installed
/// detector, the generator's manifest with its generated and hand-written sources, the baselines,
/// and one scripted detector run per scope that writes the report jscpd would write.
/// </summary>
internal sealed class CloneScenario
{
    internal const string ManifestPath = "src/OpenCode.Sdk/.generated-manifest.json";
    internal const string KnownFingerprint = "0123456789abcdef";

    private const string UsingHeader = """
        using System.Collections.Generic;
        using System.Collections.Concurrent;
        using System.Collections.Immutable;
        using System.IO;
        using System.Linq;
        using System.Text;
        using System.Threading;
        using System.Threading.Tasks;
        using static System.Math;
        using Builder = System.Text.StringBuilder;
        global using System.Net.Http;
        global using static System.Console;
        global using Tasks = System.Threading.Tasks;

        """;

    private const string ResourceBody = """
        {
            public string ReadResources(string firstPath, string secondPath)
            {
                using var first = File.OpenRead(firstPath);
                using (var second = File.OpenRead(secondPath))
                {
                    var firstLength = first.Length;
                    var secondLength = second.Length;
                    var totalLength = firstLength + secondLength;
                    var buffer = new byte[totalLength];
                    first.ReadExactly(buffer.AsSpan(0, (int)firstLength));
                    second.ReadExactly(buffer.AsSpan((int)firstLength));
                    return Convert.ToBase64String(buffer);
                }
            }
        }
        """;

    private const string StringLiteralBody = """
        {
            public string DescribeImports(string name)
            {
                var first = "using System;";
                var second = "using System.Text;";
                var third = "using static System.Math;";
                var fourth = "using Builder = System.Text.StringBuilder;";
                var fifth = "global using System.Net.Http;";
                var imports = new[] { first, second, third, fourth, fifth };
                var description = string.Join(Environment.NewLine, imports);
                return name + Environment.NewLine + description;
            }
        }
        """;

    private readonly List<string> _generated = [];
    private readonly List<string> _handWritten = [];
    private readonly RealFileSystem _detectorFileSystem = new();
    private readonly Dictionary<string, IReadOnlyList<string>> _baselineFingerprints = new(StringComparer.Ordinal);
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

    /// <summary>Exercises the emitted config against the installed, pinned detector, not a regex substitute.</summary>
    public async Task<CloneScopeResult> DetectUsingPairAsync(bool resourceBody)
    {
        var fileSystem = _detectorFileSystem;
        var root = new RepositoryRoot(fileSystem).Locate();
        var detector = new CloneDetector(fileSystem, new CliWrapProcessRunner());
        detector.EnsureInstalled(root);
        var work = CreateDetectorWorkDirectory("opencode-using-clones-");
        var sources = fileSystem.Path.Combine(work, "sources");
        try
        {
            await fileSystem.File.WriteAllTextAsync(fileSystem.Path.Combine(sources, "First.cs"),
                UsingHeader + "internal sealed class First" + (resourceBody ? ResourceBody : " { "
                    + string.Join('\n', Enumerable.Range(0, 20).Select(static index => string.Create(CultureInfo.InvariantCulture, $"public int Value{index} => {index};"))) + " }"));
            await fileSystem.File.WriteAllTextAsync(fileSystem.Path.Combine(sources, "Second.cs"),
                UsingHeader + (resourceBody ? "public sealed class Second" + ResourceBody : "public enum Second { "
                    + string.Join(',', Enumerable.Range(0, 20).Select(static index => string.Create(CultureInfo.InvariantCulture, $"Member{index} = {index}"))) + " }"));
            var scope = new CloneScope("using-pair", sources, CloneScopeFilter.None, [], Baseline: null, Threshold: null);
            return await detector.RunAsync(root, scope, new CloneSourceSplit([], []), work, updateBaseline: false, CancellationToken.None);
        }
        finally
        {
            fileSystem.Directory.Delete(work, recursive: true);
        }
    }

    /// <summary>Compares one body clone's tokens with the emitted ignore pattern and with no ignore pattern.</summary>
    public async Task<IReadOnlyList<int>> DetectStringLiteralPairTokensAsync()
    {
        var fileSystem = _detectorFileSystem;
        var root = new RepositoryRoot(fileSystem).Locate();
        var runner = new CliWrapProcessRunner();
        var detector = new CloneDetector(fileSystem, runner);
        detector.EnsureInstalled(root);
        var work = CreateDetectorWorkDirectory("opencode-string-clones-");
        var sources = fileSystem.Path.Combine(work, "sources");
        try
        {
            await fileSystem.File.WriteAllTextAsync(fileSystem.Path.Combine(sources, "First.cs"), "internal sealed class First" + StringLiteralBody);
            await fileSystem.File.WriteAllTextAsync(fileSystem.Path.Combine(sources, "Second.cs"), "public sealed class Second" + StringLiteralBody);
            var scope = new CloneScope("string-pair", sources, CloneScopeFilter.None, [], Baseline: null, Threshold: null);
            _ = await detector.RunAsync(root, scope, new CloneSourceSplit([], []), work, updateBaseline: false, CancellationToken.None);
            var ignoredTokens = await ReadSingleCloneTokensAsync(detector.ReportPath(work, scope));
            var config = fileSystem.Path.Combine(work, scope.Name + ".json");
            var document = System.Text.Json.Nodes.JsonNode.Parse(await fileSystem.File.ReadAllTextAsync(config))!.AsObject();
            _ = document.Remove("ignorePattern");
            await fileSystem.File.WriteAllTextAsync(config, document.ToJsonString());
            var result = await runner.RunAsync("bun", CloneDetector.Arguments(scope, config, updateBaseline: false), root, CancellationToken.None);
            if (result.ExitCode != 0)
            {
                throw new InvalidOperationException(result.StandardError);
            }

            return [ignoredTokens, await ReadSingleCloneTokensAsync(detector.ReportPath(work, scope))];
        }
        finally
        {
            fileSystem.Directory.Delete(work, recursive: true);
        }
    }

    private string CreateDetectorWorkDirectory(string prefix)
    {
        var fileSystem = _detectorFileSystem;
        var work = fileSystem.Path.Combine(fileSystem.Path.GetTempPath(), prefix + Guid.NewGuid().ToString("N"));
        fileSystem.Directory.CreateDirectory(fileSystem.Path.Combine(work, "sources"));
        return work;
    }

    private async Task<int> ReadSingleCloneTokensAsync(string report)
    {
        using var document = JsonDocument.Parse(await _detectorFileSystem.File.ReadAllTextAsync(report));
        return document.RootElement.GetProperty("duplicates").EnumerateArray().Single().GetProperty("tokens").GetInt32();
    }

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

    public CloneScenario WithBaseline(string scope, params string[] fingerprints)
    {
        _baselineFingerprints[scope] = fingerprints;
        return this;
    }

    /// <summary>Scripts the next detector run: its exit code and standard error, and the report totals it writes, unless it writes none.</summary>
    public CloneScenario WithRun(string scope, int exitCode = 0, int clones = 0, int newClones = 0, string standardError = "", bool writesReport = true, bool updateBaseline = false)
    {
        var result = new ProcessResult { ExitCode = exitCode, StandardOutput = [], StandardError = standardError };
        Runner.Expect("bun", () =>
            {
                var config = FileSystem.Directory.EnumerateFiles(FileSystem.Path.GetTempPath(), scope + ".json", SearchOption.AllDirectories).Single();
                List<string> tokens = ["tools/jscpd/node_modules/jscpd/run-jscpd.js",
                    scope switch { "src" or "generated" => "src", "tools" => "tools", _ => "tests" }, "--config", config, "--fail-on-empty"];
                if (scope != "generated")
                {
                    tokens.AddRange(["--baseline", scope switch
                    {
                        "src" => "tools/jscpd/src.baseline.json",
                        "tools" => "tools/jscpd/tools.baseline.json",
                        _ => "tools/jscpd/tests.baseline.json",
                    }]);
                    tokens.AddRange(!_baselines || updateBaseline ? ["--update-baseline"] : scope switch
                    {
                        "tools" => ["--fail-on-new-clones", "--threshold", "3"],
                        "tests" => ["--fail-on-new-clones", "--threshold", "7"],
                        _ => ["--fail-on-new-clones"],
                    });
                }

                return tokens;
            }, Root, result,
            writesReport ? async () =>
            {
                await WriteReportAsync(scope, clones, newClones);
                if (updateBaseline && scope != "generated")
                {
                    await WriteBaselineAsync(scope, [KnownFingerprint]);
                }
            }
        : null);
        return this;
    }

    /// <summary>Scripts a passing run for every scope, in the gate's order.</summary>
    public CloneScenario WithPassingRuns(bool updateBaseline = false)
    {
        foreach (var scope in CloneScopes.All)
        {
            WithRun(scope.Name, updateBaseline: updateBaseline);
        }

        return this;
    }

    public CloneScenario WithNewCloneFailure() => WithRun("src", exitCode: 1, clones: 5, newClones: 1,
        standardError: "Using config from work/src.json\nERROR: jscpd found 1 new clones not in the baseline (allowed: 0)")
        .WithRun("tools").WithRun("tests").WithRun("generated");

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
            foreach (var scope in CloneScopes.All.Where(static scope => scope.Baseline is not null).Select(static scope => scope.Name))
            {
                await WriteBaselineAsync(scope, _baselineFingerprints.GetValueOrDefault(scope) ?? []);
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

    private Task WriteBaselineAsync(string scope, IReadOnlyList<string> fingerprints) =>
        WriteAsync(CloneScopes.All.Single(candidate => candidate.Name == scope).Baseline!,
            JsonSerializer.Serialize(new { version = 1, fingerprints = fingerprints.ToDictionary(static fingerprint => fingerprint, static _ => 1, StringComparer.Ordinal) }));

    /// <summary>
    /// One duplicate as jscpd reports it: two fragments named relative to the scanned path (with the
    /// platform's separator, as jscpd writes them on Windows), its token count, and whether the
    /// baseline holds it.
    /// </summary>
    internal static string Duplicate(string first, string second, bool isNew) =>
        $$"""{ "firstFile": { "name": "NestedFolderPastTheWrapColumn\\{{first}}", "start": 3, "end": 33 }, "secondFile": { "name": "NestedFolderPastTheWrapColumn\\{{second}}", "start": 5, "end": 35 }, "tokens": 212, "lines": 31, "isNew": {{(isNew ? "true" : "false")}} }""";

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
        var sarif = new
        {
            runs = new[]
            {
                new
                {
                    results = new[]
                    {
                        new { partialFingerprints = new Dictionary<string, string>(StringComparer.Ordinal) { ["jscpdCloneHash/v1"] = KnownFingerprint } },
                    },
                },
            },
        };
        await FileSystem.File.WriteAllTextAsync(FileSystem.Path.Combine(output, "jscpd-report.sarif"), JsonSerializer.Serialize(sarif));
    }
}
