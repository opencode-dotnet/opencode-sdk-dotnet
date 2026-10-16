using System.IO.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// An isolated per-run directory, deleted best-effort on dispose. <see cref="TestRunRootLocation"/>
/// decides where it lives.
/// </summary>
internal sealed class TestRunRoot : IDisposable
{
    /// <summary>
    /// Short on purpose: the run root is the prefix of every path the server and git build beneath
    /// it, and Windows path limits are counted from here.
    /// </summary>
    private const int IdentifierLength = 12;

    private readonly IFileSystem _fileSystem;

    public TestRunRoot(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        _fileSystem = fileSystem;
        Path = fileSystem.Path.Combine(
            TestRunRootLocation.ForCurrentProcess(fileSystem).Resolve(),
            Guid.NewGuid().ToString("N")[..IdentifierLength]);
        _ = fileSystem.Directory.CreateDirectory(Path);
    }

    public string Path { get; }

    public string CreateSubdirectory(string name)
    {
        var directory = _fileSystem.Path.Combine(Path, name);
        _ = _fileSystem.Directory.CreateDirectory(directory);
        return directory;
    }

    public void Dispose()
    {
        // Discarded deliberately: a straggling child handle winning the race is the one outcome
        // this root has no answer for, and retention is harmless where failing a whole test run
        // over a scratch directory is not. Whatever is left sits outside every repository and the
        // user profile, where nothing reads it.
        _ = BestEffortDelete.TryDeleteTree(_fileSystem, Path);
    }
}
