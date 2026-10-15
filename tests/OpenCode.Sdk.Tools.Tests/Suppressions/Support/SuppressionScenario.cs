using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.Suppressions.Support;

/// <summary>A small repository on the filesystem double, scanned the way the ledger scans the real one.</summary>
internal sealed class SuppressionScenario
{
    private readonly MockFileSystem _fileSystem = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    /// <summary>Adds one repository file from its lines, joined with line feeds as the repository stores them.</summary>
    public SuppressionScenario WithFile(string path, params string[] lines)
    {
        _files[path] = string.Join('\n', lines) + "\n";
        return this;
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

        return await new SuppressionScan(_fileSystem).ReadAsync(root, CancellationToken.None);
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
