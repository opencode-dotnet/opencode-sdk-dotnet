using System.IO.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>An isolated workspace directory under the fixture's per-run root.</summary>
public sealed class TestWorkspace : IDisposable
{
    private readonly IFileSystem _fileSystem;

    public TestWorkspace(IFileSystem fileSystem, string runRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        _fileSystem = fileSystem;
        Path = fileSystem.Path.GetFullPath(
            fileSystem.Path.Combine(runRoot, "workspaces", Guid.NewGuid().ToString("N")));
        _ = fileSystem.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    /// <summary>Writes one text file below the workspace, creating its parent directories.</summary>
    public string WriteTextFile(string relativePath, string content)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(content);

        var filePath = ResolvePathBelow(Path, relativePath);
        var directory = _fileSystem.Path.GetDirectoryName(filePath)!;
        _ = _fileSystem.Directory.CreateDirectory(directory);
        _fileSystem.File.WriteAllText(filePath, content);
        return filePath;
    }

    /// <summary>Checks an independently seeded marker through a server-reported directory.</summary>
    /// <summary>Checks that a file below the workspace holds exactly <paramref name="expected"/>.</summary>
    public bool HasBytes(string relativePath, IReadOnlyList<byte> expected)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(expected);

        try
        {
            return _fileSystem.File.ReadAllBytes(ResolvePathBelow(Path, relativePath)).SequenceEqual(expected);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    public bool HasTextFile(string directory, string relativePath, string expectedContent)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);
        ArgumentException.ThrowIfNullOrWhiteSpace(relativePath);
        ArgumentNullException.ThrowIfNull(expectedContent);

        try
        {
            return string.Equals(
                _fileSystem.File.ReadAllText(ResolvePathBelow(directory, relativePath)),
                expectedContent,
                StringComparison.Ordinal);
        }
        catch (FileNotFoundException)
        {
            return false;
        }
        catch (DirectoryNotFoundException)
        {
            return false;
        }
    }

    private string ResolvePathBelow(string root, string relativePath)
    {
        if (_fileSystem.Path.IsPathRooted(relativePath))
        {
            throw new ArgumentException("The path must remain below the supplied directory.", nameof(relativePath));
        }

        var absoluteRoot = _fileSystem.Path.GetFullPath(root);
        var filePath = _fileSystem.Path.GetFullPath(_fileSystem.Path.Combine(absoluteRoot, relativePath));
        var rootPrefix = absoluteRoot + _fileSystem.Path.DirectorySeparatorChar;
        var comparison = _fileSystem.Path.DirectorySeparatorChar == '\\'
            ? StringComparison.OrdinalIgnoreCase
            : StringComparison.Ordinal;
        if (!filePath.StartsWith(rootPrefix, comparison))
        {
            throw new ArgumentException("The path must remain below the supplied directory.", nameof(relativePath));
        }

        return filePath;
    }

    public void Dispose()
    {
        // Discarded deliberately: this workspace lives under the run root, so a tree a straggling
        // child handle keeps alive here is swept again when that root is disposed.
        _ = BestEffortDelete.TryDeleteTree(_fileSystem, Path);
    }
}
