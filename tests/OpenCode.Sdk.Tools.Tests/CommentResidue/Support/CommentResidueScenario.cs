using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.CommentResidue.Support;

/// <summary>A small repository on the filesystem double, scanned the way the gate scans the real one.</summary>
internal sealed class CommentResidueScenario
{
    private readonly MockFileSystem _fileSystem = new();
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    /// <summary>Adds one repository file from its lines, joined with line feeds as the repository stores them.</summary>
    public CommentResidueScenario WithFile(string path, params string[] lines)
    {
        _files[path] = string.Join('\n', lines) + "\n";
        return this;
    }

    /// <summary>Every hit of one scan, as the gate prints it.</summary>
    public async Task<IReadOnlyList<string>> ScanAsync()
    {
        var root = _fileSystem.Path.GetFullPath("repo");
        _fileSystem.Directory.CreateDirectory(root);
        foreach (var (path, text) in _files)
        {
            var fullPath = _fileSystem.Path.Combine(root, path);
            _fileSystem.Directory.CreateDirectory(_fileSystem.Path.GetDirectoryName(fullPath)!);
            await _fileSystem.File.WriteAllTextAsync(fullPath, text);
        }

        var hits = await new CommentResidueScan(_fileSystem).ReadAsync(root, CancellationToken.None);
        return [.. hits.Select(static hit => hit.ToString())];
    }
}
