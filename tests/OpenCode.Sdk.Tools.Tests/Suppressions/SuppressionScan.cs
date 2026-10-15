using System.IO.Abstractions;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// Reads every suppression channel in the repository: C# sources, MSBuild files, analyzer configs,
/// the command lines of the CI workflows and actions, and the slopwatch baseline, with the project
/// membership a project-wide warning list needs. A channel the ledger does not read is refused when
/// it appears: a rule set, a Slopwatch file other than the baseline, and a global config with
/// path sections.
/// </summary>
internal sealed class SuppressionScan(IFileSystem fileSystem)
{
    internal const string SlopwatchBaselinePath = ".slopwatch/baseline.json";

    private const string SlopwatchFolder = ".slopwatch/";
    private const string WorkflowFolder = ".github/";
    private const string EditorConfigFileName = ".editorconfig";
    private const string GlobalConfigExtension = ".globalconfig";
    private static readonly string[] MSBuildExtensions = [".csproj", ".props", ".targets"];
    private static readonly string[] WorkflowExtensions = [".yml", ".yaml"];

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public async Task<SuppressionInventory> ReadAsync(string root, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var files = new RepositoryFileSet(_fileSystem).Enumerate(root);
        var suppressions = new List<Suppression>();
        var configs = new List<AnalyzerConfigDocument>();
        var compileIncludes = new Dictionary<string, IReadOnlyList<string>>(StringComparer.Ordinal);
        foreach (var file in files)
        {
            var name = file[(file.LastIndexOf('/', StringComparison.Ordinal) + 1)..];
            if (file.EndsWith(".cs", StringComparison.Ordinal))
            {
                suppressions.AddRange(CSharpSuppressionReader.Read(file, await ReadAsync(root, file, cancellationToken)));
            }
            else if (MSBuildExtensions.Any(extension => file.EndsWith(extension, StringComparison.Ordinal)))
            {
                var text = await ReadAsync(root, file, cancellationToken);
                suppressions.AddRange(ProjectSuppressionReader.Read(file, text));
                if (file.EndsWith(".csproj", StringComparison.Ordinal))
                {
                    compileIncludes.Add(file, ProjectSuppressionReader.CompileIncludes(text));
                }
            }
            else if (string.Equals(name, EditorConfigFileName, StringComparison.Ordinal) || name.EndsWith(GlobalConfigExtension, StringComparison.Ordinal))
            {
                var config = AnalyzerConfigReader.Parse(file, AnalyzerConfigAudit.FullPath(root, file), await ReadAsync(root, file, cancellationToken));
                if (config.IsGlobal && config.Sections.Count > 0)
                {
                    throw new InvalidOperationException($"{file} is a global config with path sections, which the suppression ledger does not read.");
                }

                configs.Add(config);
                suppressions.AddRange(AnalyzerConfigReader.Suppressions(config));
            }
            else if (file.StartsWith(WorkflowFolder, StringComparison.Ordinal) && WorkflowExtensions.Any(extension => file.EndsWith(extension, StringComparison.Ordinal)))
            {
                suppressions.AddRange(ProjectSuppressionReader.CommandLine(file, await ReadAsync(root, file, cancellationToken)));
            }
            else if (string.Equals(file, SlopwatchBaselinePath, StringComparison.Ordinal))
            {
                suppressions.AddRange(SlopwatchBaselineReader.Read(file, await ReadAsync(root, file, cancellationToken)));
            }
            else if (file.StartsWith(SlopwatchFolder, StringComparison.Ordinal) || file.EndsWith(".ruleset", StringComparison.OrdinalIgnoreCase))
            {
                throw new InvalidOperationException($"{file} can suppress diagnostics, and the suppression ledger does not read it; extend the ledger before adding it.");
            }
        }

        return new SuppressionInventory(root, files, suppressions, configs, new ProjectMembership(files, compileIncludes));
    }

    private Task<string> ReadAsync(string root, string file, CancellationToken cancellationToken) =>
        _fileSystem.File.ReadAllTextAsync(_fileSystem.Path.Combine(root, file), cancellationToken);
}
