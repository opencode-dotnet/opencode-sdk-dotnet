using System.IO.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// Reads every suppression channel in the repository: C# sources, MSBuild files, analyzer configs
/// and the slopwatch baseline, with the project membership a project-wide warning list needs.
/// </summary>
internal sealed class SuppressionScan(IFileSystem fileSystem)
{
    internal const string SlopwatchBaselinePath = ".slopwatch/baseline.json";

    private const string EditorConfigFileName = ".editorconfig";
    private const string GlobalConfigExtension = ".globalconfig";
    private static readonly string[] MSBuildExtensions = [".csproj", ".props", ".targets"];

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
                var config = AnalyzerConfigReader.Parse(file, FullPath(root, file), await ReadAsync(root, file, cancellationToken));
                configs.Add(config);
                suppressions.AddRange(AnalyzerConfigReader.Suppressions(config));
            }
            else if (string.Equals(file, SlopwatchBaselinePath, StringComparison.Ordinal))
            {
                suppressions.AddRange(SlopwatchBaselineReader.Read(file, await ReadAsync(root, file, cancellationToken)));
            }
        }

        return new SuppressionInventory(root, files, suppressions, configs, new ProjectMembership(files, compileIncludes));
    }

    private string FullPath(string root, string file) => _fileSystem.Path.Combine(root, file);

    private Task<string> ReadAsync(string root, string file, CancellationToken cancellationToken) =>
        _fileSystem.File.ReadAllTextAsync(FullPath(root, file), cancellationToken);
}
