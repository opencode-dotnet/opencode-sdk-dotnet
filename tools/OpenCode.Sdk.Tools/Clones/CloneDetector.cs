using System.IO.Abstractions;
using System.Text.Json;
using OpenCode.Sdk.Tools.Generator;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;
using OpenCode.Sdk.Tools.Output;
using OpenCode.Sdk.Tools.Serialization;

namespace OpenCode.Sdk.Tools.Clones;

/// <summary>
/// Runs the pinned jscpd over one clone scope: it writes the scope's jscpd config into a work
/// folder, runs the detector through the pinned Bun from the repository root, and reads the totals of
/// the JSON report it writes. A gated scope runs against its baseline and fails on a new clone or
/// stale baseline entry or above its threshold; with an update it rewrites the baseline instead. Every scope fails when it
/// analyses no file. jscpd's own defaults stay: it honours .gitignore, follows no symbolic link, and
/// skips files above 1 MiB.
/// </summary>
internal sealed class CloneDetector(IFileSystem fileSystem, IProcessRunner processRunner)
{
    internal const string RunnerPath = CloneScopes.DetectorFolder + "/node_modules/jscpd/run-jscpd.js";
    internal const string ReportFileName = "jscpd-report.json";

    /// <summary>jscpd otherwise writes an empty report and exits 0 when nothing was analysed, which would read as a clean scope.</summary>
    private const string FailOnEmpty = "--fail-on-empty";
    private const int MinimumTokens = 50;
    private static readonly string[] CommonIgnore = ["**/bin/**", "**/obj/**", "**/node_modules/**"];

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));

    /// <summary>Refuses unless the pinned detector is installed under the repository root.</summary>
    public void EnsureInstalled(string root)
    {
        if (!_fileSystem.File.Exists(_fileSystem.Path.Combine(root, RunnerPath)))
        {
            throw new CloneDetectorException(
                "The clone detector is not installed. Run once, with the pinned Bun first on PATH: "
                + "cd " + CloneScopes.DetectorFolder + " && bun install --frozen-lockfile --ignore-scripts");
        }
    }

    /// <summary>Splits the C# files under the SDK's generation root by the generator's manifest.</summary>
    public async Task<CloneSourceSplit> ReadSourceSplitAsync(string root, CancellationToken cancellationToken)
    {
        var outputRoot = GenerationRequest.RepositoryDefault(verify: false).OutputRoot;
        var manifestPath = _fileSystem.Path.Combine(root, outputRoot, GenerationWriter.ManifestRelativePath);
        if (!_fileSystem.File.Exists(manifestPath))
        {
            throw new CloneDetectorException($"The generator's manifest is missing at '{outputRoot}/{GenerationWriter.ManifestRelativePath}'.");
        }

        GenerationManifest? manifest;
        try
        {
            var json = await _fileSystem.File.ReadAllTextAsync(manifestPath, cancellationToken).ConfigureAwait(false);
            manifest = JsonSerializer.Deserialize(json, ToolJsonContext.Default.GenerationManifest);
        }
        catch (JsonException exception)
        {
            throw new CloneDetectorException($"The generator's manifest at '{outputRoot}/{GenerationWriter.ManifestRelativePath}' is not a file list.", exception);
        }

        var generated = (manifest ?? throw new CloneDetectorException("The generator's manifest is JSON null."))
            .Files.Select(file => outputRoot + "/" + file).ToHashSet(StringComparer.Ordinal);
        var handWritten = SourceFiles(root, "src").Where(file => !generated.Contains(file)).ToList();
        return new CloneSourceSplit([.. generated.Order(StringComparer.Ordinal)], handWritten);
    }

    /// <summary>Runs one scope from the repository root, with its config and report in <paramref name="workDirectory"/>.</summary>
    public async Task<CloneScopeResult> RunAsync(
        string root, CloneScope scope, CloneSourceSplit split, string workDirectory, bool updateBaseline, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(scope);
        ArgumentNullException.ThrowIfNull(split);

        var output = _fileSystem.Path.Combine(workDirectory, scope.Name);
        var config = _fileSystem.Path.Combine(workDirectory, scope.Name + ".json");
        await WriteConfigAsync(config, output, Ignore(scope, split), cancellationToken).ConfigureAwait(false);

        var result = await _processRunner.RunAsync("bun", Arguments(scope, config, updateBaseline), root, cancellationToken).ConfigureAwait(false);
        var report = _fileSystem.Path.Combine(output, ReportFileName);
        if (!_fileSystem.File.Exists(report))
        {
            throw new CloneDetectorException(string.Create(
                System.Globalization.CultureInfo.InvariantCulture,
                $"jscpd wrote no report for scope '{scope.Name}' (exit {result.ExitCode}): {result.StandardError.Trim()}"));
        }

        using var document = JsonDocument.Parse(await _fileSystem.File.ReadAllTextAsync(report, cancellationToken).ConfigureAwait(false));
        var total = document.RootElement.GetProperty("statistics").GetProperty("total");
        return new CloneScopeResult(
            scope,
            result.ExitCode,
            total.GetProperty("clones").GetInt32(),
            total.GetProperty("duplicatedLines").GetInt32(),
            total.GetProperty("lines").GetInt32(),
            total.GetProperty("percentage").GetDouble(),
            total.TryGetProperty("newClones", out var newClones) ? newClones.GetInt32() : 0,
            NewCloneLocations(scope, document.RootElement),
            scope.Baseline is not null && !updateBaseline
                ? await StaleFingerprintsAsync(root, scope.Baseline, output, cancellationToken).ConfigureAwait(false)
                : [],
            result.StandardError.Trim());
    }

    /// <summary>The path of the report a run of <paramref name="scope"/> writes into <paramref name="workDirectory"/>.</summary>
    public string ReportPath(string workDirectory, CloneScope scope)
    {
        ArgumentNullException.ThrowIfNull(scope);

        return _fileSystem.Path.Combine(workDirectory, scope.Name, ReportFileName);
    }

    /// <summary>Uses the detector's own content fingerprints from the same scan, rather than reproducing its hash algorithm.</summary>
    private async Task<IReadOnlyList<string>> StaleFingerprintsAsync(string root, string baseline, string output, CancellationToken cancellationToken)
    {
        using var accepted = JsonDocument.Parse(await _fileSystem.File.ReadAllTextAsync(_fileSystem.Path.Combine(root, baseline), cancellationToken).ConfigureAwait(false));
        using var reported = JsonDocument.Parse(await _fileSystem.File.ReadAllTextAsync(_fileSystem.Path.Combine(output, "jscpd-report.sarif"), cancellationToken).ConfigureAwait(false));
        var current = reported.RootElement.GetProperty("runs").EnumerateArray()
            .SelectMany(static run => run.GetProperty("results").EnumerateArray())
            .Select(static result => result.GetProperty("partialFingerprints").GetProperty("jscpdCloneHash/v1").GetString()!)
            .ToHashSet(StringComparer.Ordinal);
        return [.. accepted.RootElement.GetProperty("fingerprints").EnumerateObject()
            .Select(static entry => entry.Name).Except(current, StringComparer.Ordinal).Order(StringComparer.Ordinal)];
    }

    /// <summary>
    /// Where each clone the baseline does not hold sits. jscpd marks those duplicates <c>isNew</c> and
    /// names their files relative to the scanned path, so the scope's path is put in front.
    /// </summary>
    private static List<CloneLocation> NewCloneLocations(CloneScope scope, JsonElement report) =>
        !report.TryGetProperty("duplicates", out var duplicates)
            ? []
            :
            [
                .. duplicates.EnumerateArray()
                    .Where(static duplicate => duplicate.TryGetProperty("isNew", out var isNew) && isNew.ValueKind == JsonValueKind.True)
                    .Select(duplicate => new CloneLocation(
                        Fragment(scope, duplicate.GetProperty("firstFile")),
                        Fragment(scope, duplicate.GetProperty("secondFile")),
                        duplicate.GetProperty("tokens").GetInt32())),
            ];

    private static string Fragment(CloneScope scope, JsonElement file) =>
        string.Create(
            System.Globalization.CultureInfo.InvariantCulture,
            $"{scope.Path}/{file.GetProperty("name").GetString()!.Replace('\\', '/')}:{file.GetProperty("start").GetInt32()}-{file.GetProperty("end").GetInt32()}");

    internal static IReadOnlyList<string> Arguments(CloneScope scope, string config, bool updateBaseline)
    {
        ArgumentNullException.ThrowIfNull(scope);

        List<string> arguments = [RunnerPath, scope.Path, "--config", config, FailOnEmpty];
        if (scope.Baseline is null)
        {
            return arguments;
        }

        arguments.AddRange(["--baseline", scope.Baseline]);
        if (updateBaseline)
        {
            arguments.Add("--update-baseline");
            return arguments;
        }

        arguments.Add("--fail-on-new-clones");
        if (scope.Threshold is { } threshold)
        {
            arguments.AddRange(["--threshold", threshold.ToString(System.Globalization.CultureInfo.InvariantCulture)]);
        }

        return arguments;
    }

    private static IReadOnlyList<string> Ignore(CloneScope scope, CloneSourceSplit split) =>
    [
        .. CommonIgnore,
        .. scope.Ignore,
        .. scope.Filter switch
        {
            CloneScopeFilter.Generated => split.Generated,
            CloneScopeFilter.HandWritten => split.HandWritten,
            CloneScopeFilter.None => [],
            _ => throw new ArgumentOutOfRangeException(nameof(scope), scope.Filter, "Unknown clone scope filter."),
        },
    ];

    private async Task WriteConfigAsync(string config, string output, IReadOnlyList<string> ignore, CancellationToken cancellationToken)
    {
        using var buffer = new MemoryStream();
        var writer = new Utf8JsonWriter(buffer, new JsonWriterOptions { Indented = true });
        await using (writer.ConfigureAwait(false))
        {
            writer.WriteStartObject();
            writer.WriteStartArray("ignore");
            foreach (var glob in ignore)
            {
                writer.WriteStringValue(glob);
            }

            writer.WriteEndArray();
            writer.WriteStartArray("ignorePattern");
            writer.WriteStringValue(@"(?m)^[ \t]*(global[ \t]+)?using[ \t]+(static[ \t]+)?([A-Za-z_][A-Za-z0-9_]*[ \t]*=[ \t]*)?[A-Za-z_][A-Za-z0-9_.]*[ \t]*;");
            writer.WriteEndArray();
            writer.WriteStartArray("format");
            writer.WriteStringValue("csharp");
            writer.WriteEndArray();
            writer.WriteNumber("minTokens", MinimumTokens);
            writer.WriteStartArray("reporters");
            writer.WriteStringValue("console");
            writer.WriteStringValue("json");
            writer.WriteStringValue("sarif");
            writer.WriteEndArray();
            writer.WriteString("output", output);
            writer.WriteEndObject();
        }

        await _fileSystem.File.WriteAllBytesAsync(config, buffer.ToArray(), cancellationToken).ConfigureAwait(false);
    }

    private IEnumerable<string> SourceFiles(string root, string folder)
    {
        var directory = _fileSystem.Path.Combine(root, folder);
        return !_fileSystem.Directory.Exists(directory)
            ? []
            : _fileSystem.Directory.EnumerateFiles(directory, "*.cs", SearchOption.AllDirectories)
                .Select(path => _fileSystem.Path.GetRelativePath(root, path).Replace('\\', '/'))
                .Where(static path => path.EndsWith(".cs", StringComparison.Ordinal)
                    && !path.Split('/').Any(static part => part is "bin" or "obj"))
                .Order(StringComparer.Ordinal);
    }
}
