using OpenCode.Sdk.Tests.Harness;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests;

public sealed class BestEffortDeleteTests
{
    private readonly RealFileSystem _fileSystem = new();

    /// <summary>
    /// Git writes its object files read-only, and on Windows a recursive delete refuses a read-only
    /// file; a run root holding a test repository then stayed behind after every run. A read-only
    /// entry is not the contention the delete tolerates, so it must not survive.
    /// </summary>
    [Test]
    public async Task TryDeleteTree_Should_Remove_A_Tree_Holding_A_ReadOnly_File()
    {
        var root = _fileSystem.Path.Combine(
            _fileSystem.Path.GetTempPath(), "opencode-sdk-best-effort-" + Guid.NewGuid().ToString("N"));
        var objects = _fileSystem.Path.Combine(root, "repository", ".git", "objects", "37");
        _ = _fileSystem.Directory.CreateDirectory(objects);
        var blob = _fileSystem.Path.Combine(objects, "ef313cc3ab6f652163a11b331e9b4c768c2d42");
        _fileSystem.File.WriteAllText(blob, "blob");
        _fileSystem.File.SetAttributes(blob, FileAttributes.ReadOnly);

        try
        {
            var deleted = BestEffortDelete.TryDeleteTree(_fileSystem, root);

            await Assert.That(deleted).IsTrue();
            await Assert.That(_fileSystem.Directory.Exists(root)).IsFalse();
        }
        finally
        {
            if (_fileSystem.File.Exists(blob))
            {
                _fileSystem.File.SetAttributes(blob, FileAttributes.Normal);
                _fileSystem.Directory.Delete(root, recursive: true);
            }
        }
    }
}
