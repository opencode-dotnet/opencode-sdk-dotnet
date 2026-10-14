using System.Text.Json;
using Testably.Abstractions.Testing;
using Testably.Abstractions.Testing.Initializer;

namespace OpenCode.Sdk.Tools.Tests.TestRoster.Support;

/// <summary>A verify-test-roster scenario: the committed roster file plus one TRX per module that
/// ran. Roster legs and TRX files are declared in domain terms; the JSON and XML live here.</summary>
internal sealed class TestRosterScenario
{
    public const string RosterPath = "tests/test-roster.json";
    public const string ResultsDirectory = "test-results/main";

    private readonly Dictionary<string, Dictionary<string, List<string>>> _passes = new(StringComparer.Ordinal);

    private readonly List<(string RelativePath, string Content)> _files = [];

    public TestRosterScenario WithEntry(string passName, string operatingSystem, params string[] modules)
    {
        if (!_passes.TryGetValue(passName, out var operatingSystems))
        {
            operatingSystems = new Dictionary<string, List<string>>(StringComparer.Ordinal);
            _passes[passName] = operatingSystems;
        }

        if (!operatingSystems.TryGetValue(operatingSystem, out var passModules))
        {
            passModules = [];
            operatingSystems[operatingSystem] = passModules;
        }

        passModules.AddRange(modules);
        return this;
    }

    public TestRosterScenario WithTrx(string assembly, string targetFramework, int total, int executed, string arch = "x64") =>
        WithFile($"{assembly}_{targetFramework}_{arch}.trx", TrxContent.WithCounters(total, executed));

    public TestRosterScenario WithFile(string relativePath, string content)
    {
        _files.Add((relativePath, content));
        return this;
    }

    public MockFileSystem Build()
    {
        var fileSystem = new MockFileSystem();
        fileSystem.Initialize().With(
        [
            new FileDescription(RosterPath, JsonSerializer.Serialize(new { passes = _passes })),
            .. _files.Select(file => new FileDescription(
                fileSystem.Path.Combine(
                    ResultsDirectory,
                    file.RelativePath.Replace('/', fileSystem.Path.DirectorySeparatorChar)),
                file.Content)),
        ]);
        return fileSystem;
    }
}
