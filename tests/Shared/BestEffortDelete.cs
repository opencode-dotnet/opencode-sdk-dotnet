using System.IO.Abstractions;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// Deletes a temporary directory tree without letting the deletion itself end a test run.
/// <see cref="TestRunRoot"/> and <see cref="TestWorkspace"/> both dispose directories a launched
/// server was writing into moments earlier, so losing the race against a straggling child handle
/// is expected rather than exceptional. Only that contention is absorbed: the failure is reported
/// as a false result, and anything else the file system raises is a real defect and propagates.
/// A read-only entry is not contention: Windows refuses to delete one, and git writes every object
/// file read-only, so a tree whose delete is refused has its read-only attributes cleared and is
/// deleted once more.
/// </summary>
internal static class BestEffortDelete
{
    /// <summary>Deletes the directory tree at <paramref name="path"/>, contents included.</summary>
    /// <returns>
    /// True when the tree is gone; false when a live handle or an access denial kept it, in which
    /// case the operating system's temp cleaner owns the leftovers.
    /// </returns>
    public static bool TryDeleteTree(IFileSystem fileSystem, string path)
    {
        if (TryDelete(fileSystem, path, out var refused))
        {
            return true;
        }

        return refused && ClearReadOnly(fileSystem, path) && TryDelete(fileSystem, path, out _);
    }

    private static bool TryDelete(IFileSystem fileSystem, string path, out bool refused)
    {
        refused = false;
        try
        {
            fileSystem.Directory.Delete(path, recursive: true);
            return true;
        }
        catch (IOException)
        {
            // The tree, or a file inside it, is still open: a child process outlived the fixture
            // that owned it, which retention makes harmless.
            return false;
        }
        catch (UnauthorizedAccessException)
        {
            // A read-only entry, or the same contention surfacing as an access denial on some
            // platforms; the caller tells them apart by clearing the attribute and trying again.
            refused = true;
            return false;
        }
    }

    /// <summary>Clears the read-only attribute on every file left in the tree.</summary>
    /// <returns>True when the tree could be walked; false when it changed underneath the walk.</returns>
    private static bool ClearReadOnly(IFileSystem fileSystem, string path)
    {
        try
        {
            foreach (var file in fileSystem.Directory.EnumerateFiles(path, "*", SearchOption.AllDirectories))
            {
                var attributes = fileSystem.File.GetAttributes(file);
                if (attributes.HasFlag(FileAttributes.ReadOnly))
                {
                    fileSystem.File.SetAttributes(file, attributes & ~FileAttributes.ReadOnly);
                }
            }

            return true;
        }
        catch (Exception exception) when (exception is IOException or UnauthorizedAccessException)
        {
            // An entry vanished or stayed locked mid-walk: the contention the retry could not
            // resolve either, so the tree is retained like any other contended one.
            return false;
        }
    }
}
