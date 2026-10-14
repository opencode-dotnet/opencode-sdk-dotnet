using System.Text.Json;
using System.Xml.Linq;
using Microsoft.CodeAnalysis;
using Microsoft.CodeAnalysis.CSharp;
using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.Packaging.Support;

/// <summary>A minimal repository with typed XML, manifest and C# declaration builders.</summary>
internal sealed class PackageValidationScenario
{
    private readonly MockFileSystem _fileSystem = new();
    private readonly Dictionary<string, string> _currentSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _removedSources = new(StringComparer.Ordinal);
    private readonly Dictionary<string, List<CompatibilitySuppression>> _suppressions = new(StringComparer.Ordinal);
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);
    private string _version = "0.9.0";
    private string _baseline = string.Empty;
    private bool _missingBaseline;
    private string? _missingSuppressionField;

    public ScriptedProcessRunner Runner { get; } = new();

    public PackageValidationScenario WithVersions(string version, string baseline)
    {
        _version = version;
        _baseline = baseline;
        return this;
    }

    public PackageValidationScenario WithGeneratedType(string path, string name)
    {
        _currentSources.Add(path, Source(name, partial: false));
        return this;
    }

    public PackageValidationScenario WithRemovedGeneratedType(string path, string name)
    {
        _removedSources.Add(path, Source(name, partial: false));
        return this;
    }

    public PackageValidationScenario WithHandWrittenPartial(string path, string name)
    {
        Write(PackageCompatibilityRepositoryReader.SdkPath + path, Source(name, partial: true));
        return this;
    }

    public PackageValidationScenario WithSuppression(string target, string project = "OpenCode.Sdk", bool isBaseline = true)
    {
        if (!_suppressions.TryGetValue(project, out var entries))
        {
            entries = [];
            _suppressions.Add(project, entries);
        }

        entries.Add(new CompatibilitySuppression("CP0001", target, "lib/net10.0/left.dll", "lib/net10.0/right.dll", isBaseline));
        return this;
    }

    public PackageValidationScenario WithMissingBaselineTag()
    {
        _missingBaseline = true;
        return this;
    }

    public PackageValidationScenario WithoutSuppressionField(string name)
    {
        _missingSuppressionField = name;
        return this;
    }

    public async Task<PackageCompatibilityEvidence> ReadAsync()
    {
        Write("Directory.Build.props", new XElement("Project", new XElement("PropertyGroup",
            new XElement("VersionPrefix", _version), new XElement("PackageValidationBaselineVersion", _baseline))).ToString());
        Write(PackageCompatibilityRepositoryReader.ManifestPath, Manifest(_currentSources.Keys));
        foreach (var source in _currentSources)
        {
            Write(PackageCompatibilityRepositoryReader.SdkPath + source.Key, source.Value);
        }

        foreach (var project in _suppressions)
        {
            Write($"src/{project.Key}/{project.Key}.csproj", new XElement("Project").ToString());
            var document = new XElement("Suppressions",
                project.Value.Select(static entry => new XElement("Suppression",
                    new XElement("DiagnosticId", entry.DiagnosticId), new XElement("Target", entry.Target),
                    new XElement("Left", entry.Left), new XElement("Right", entry.Right),
                    new XElement("IsBaselineSuppression", entry.IsBaselineSuppression))));
            if (_missingSuppressionField is { } field)
            {
                document.Descendants(field).Remove();
            }

            Write($"src/{project.Key}/CompatibilitySuppressions.xml", document.ToString());
        }

        if (_baseline.Length > 0)
        {
            _ = Runner.Expect("git", $"show v{_baseline}:{PackageCompatibilityRepositoryReader.ManifestPath}",
                _missingBaseline ? ScriptedProcessRunner.Fail("fatal: invalid object name")
                    : ScriptedProcessRunner.Ok(Manifest(_currentSources.Keys.Concat(_removedSources.Keys))));
            foreach (var source in _removedSources)
            {
                _ = Runner.Expect("git", $"show v{_baseline}:{PackageCompatibilityRepositoryReader.SdkPath}{source.Key}",
                    ScriptedProcessRunner.Ok(source.Value));
            }
        }

        var repository = _fileSystem.Path.GetFullPath("repository");
        foreach (var file in _files)
        {
            var fullPath = _fileSystem.Path.Combine(repository, file.Key);
            _ = _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(fullPath)!);
            await _fileSystem.File.WriteAllTextAsync(fullPath, file.Value);
        }

        return await new PackageCompatibilityRepositoryReader(_fileSystem, Runner).ReadAsync(repository, CancellationToken.None);
    }

    private static string Manifest(IEnumerable<string> paths) => JsonSerializer.Serialize(new { files = paths.ToArray() });

    private static string Source(string name, bool partial)
    {
        var declaration = SyntaxFactory.ClassDeclaration(name);
        if (partial)
        {
            declaration = declaration.AddModifiers(SyntaxFactory.Token(SyntaxKind.PartialKeyword));
        }

        return SyntaxFactory.CompilationUnit()
            .AddMembers(SyntaxFactory.FileScopedNamespaceDeclaration(SyntaxFactory.ParseName("OpenCode.Sdk.Models")).AddMembers(declaration))
            .NormalizeWhitespace().ToFullString();
    }

    /// <summary>Records a repository file; <see cref="ReadAsync"/> writes every recorded file before it reads.</summary>
    private void Write(string path, string text) => _files[path] = text;
}
