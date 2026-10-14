using System.IO.Abstractions;
using System.Text;
using System.Xml.Linq;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Packaging;

/// <summary>Reads current sources and only the baseline blobs needed to classify removed types.</summary>
internal sealed class PackageCompatibilityRepositoryReader(IFileSystem fileSystem, IProcessRunner processRunner)
{
    internal const string SdkPath = "src/OpenCode.Sdk/";
    internal const string ManifestPath = SdkPath + ".generated-manifest.json";

    private static readonly string[] ProjectFolders = ["src", "tests", "tools"];

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));
    private readonly IProcessRunner _processRunner = processRunner ?? throw new ArgumentNullException(nameof(processRunner));
    private readonly GeneratedTypeCatalog _catalog = new();

    public async Task<PackageCompatibilityEvidence> ReadAsync(string repository, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(repository);

        var props = XDocument.Parse(await ReadFileAsync(repository, "Directory.Build.props", cancellationToken));
        var version = props.Descendants("VersionPrefix").Single().Value.Trim();
        var baseline = props.Descendants("PackageValidationBaselineVersion").Single().Value.Trim();
        var files = new GeneratedManifest(await ReadFileAsync(repository, ManifestPath, cancellationToken)).Files;
        var generated = new HashSet<string>(StringComparer.Ordinal);
        foreach (var path in files)
        {
            var source = await ReadFileAsync(repository, SdkPath + path, cancellationToken);
            generated.UnionWith(_catalog.ReadDeclarations(source, path, generated: true).Select(static type => type.FullName));
        }

        if (baseline.Length > 0)
        {
            var baselineFiles = new GeneratedManifest(await ReadBaselineAsync(repository, baseline, ManifestPath, cancellationToken)).Files;
            foreach (var path in baselineFiles.Except(files, StringComparer.Ordinal))
            {
                var source = await ReadBaselineAsync(repository, baseline, SdkPath + path, cancellationToken);
                generated.UnionWith(_catalog.ReadDeclarations(source, path, generated: true).Select(static type => type.FullName));
            }
        }

        return new PackageCompatibilityEvidence
        {
            VersionPrefix = version,
            BaselineVersion = baseline,
            GeneratedTypes = generated,
            HandWrittenPartials = await ReadHandWrittenPartialsAsync(repository, files, cancellationToken),
            ProjectSuppressions = await ReadSuppressionsAsync(repository, cancellationToken),
        };
    }

    private Task<string> ReadFileAsync(string repository, string path, CancellationToken cancellationToken) =>
        _fileSystem.File.ReadAllTextAsync(_fileSystem.Path.Combine(repository, path), cancellationToken);

    private async Task<string> ReadBaselineAsync(string repository, string baseline, string path, CancellationToken cancellationToken)
    {
        var result = await _processRunner.RunAsync("git", ["show", $"v{baseline}:{path}"], repository, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException(
                $"Cannot read '{path}' at baseline tag 'v{baseline}'. Fetch tags with 'git fetch --tags' before running this check. {result.StandardError.Trim()}");
        }

        return Encoding.UTF8.GetString(result.StandardOutput);
    }

    private async Task<IReadOnlyDictionary<string, string>> ReadHandWrittenPartialsAsync(string repository,
        IReadOnlySet<string> generatedFiles, CancellationToken cancellationToken)
    {
        var sdk = _fileSystem.Path.Combine(repository, SdkPath);
        var partials = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var path in SourceFiles(sdk, "*.cs"))
        {
            var relative = _fileSystem.Path.GetRelativePath(sdk, path).Replace('\\', '/');
            if (generatedFiles.Contains(relative))
            {
                continue;
            }

            var source = await _fileSystem.File.ReadAllTextAsync(path, cancellationToken);
            foreach (var fullName in _catalog.ReadDeclarations(source, relative, generated: false)
                .Where(static type => type.IsPartial).Select(static type => type.FullName))
            {
                _ = partials.TryAdd(relative + ":" + fullName, fullName);
            }
        }

        return partials;
    }

    private async Task<IReadOnlyDictionary<string, IReadOnlyList<CompatibilitySuppression>>> ReadSuppressionsAsync(
        string repository, CancellationToken cancellationToken)
    {
        var suppressions = new Dictionary<string, IReadOnlyList<CompatibilitySuppression>>(StringComparer.Ordinal);
        foreach (var folder in ProjectFolders)
        {
            var directory = _fileSystem.Path.Combine(repository, folder);
            if (!_fileSystem.Directory.Exists(directory))
            {
                continue;
            }

            foreach (var project in SourceFiles(directory, "*.csproj"))
            {
                var path = _fileSystem.Path.Combine(_fileSystem.Path.GetDirectoryName(project)!, "CompatibilitySuppressions.xml");
                if (_fileSystem.File.Exists(path))
                {
                    var reader = new CompatibilitySuppressionReader(await _fileSystem.File.ReadAllTextAsync(path, cancellationToken));
                    suppressions.Add(_fileSystem.Path.GetFileNameWithoutExtension(project), reader.Read());
                }
            }
        }

        return suppressions;
    }

    private IEnumerable<string> SourceFiles(string root, string pattern) =>
        _fileSystem.Directory.EnumerateFiles(root, pattern, SearchOption.AllDirectories)
            .Where(path => !_fileSystem.Path.GetRelativePath(root, path).Replace('\\', '/').Split('/')
                .Any(static part => part is "obj" or "bin"));
}
