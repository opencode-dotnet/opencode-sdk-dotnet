using System.IO.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// The repository's own files, by repository-relative path with forward slashes: everything under
/// the root except version control, build output, package caches, upstream checkouts and scratch work.
/// </summary>
internal sealed class RepositoryFileSet(IFileSystem fileSystem)
{
    private static readonly string[] SkippedAnywhere = ["bin", "obj", "node_modules", ".git", ".vs", ".idea"];
    private static readonly string[] SkippedAtRoot = ["external", ".scratchpad", "artifacts", "packages", "test-results", "TestResults"];

    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public IReadOnlyList<string> Enumerate(string root)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var result = new List<string>();
        var pending = new Stack<string>([root]);
        while (pending.TryPop(out var directory))
        {
            result.AddRange(_fileSystem.Directory.EnumerateFiles(directory)
                .Select(file => _fileSystem.Path.GetRelativePath(root, file).Replace('\\', '/')));
            foreach (var child in _fileSystem.Directory.EnumerateDirectories(directory))
            {
                var name = _fileSystem.Path.GetFileName(child);
                var skipped = SkippedAnywhere.Contains(name, StringComparer.Ordinal)
                    || (string.Equals(directory, root, StringComparison.Ordinal) && SkippedAtRoot.Contains(name, StringComparer.Ordinal));
                if (!skipped)
                {
                    pending.Push(child);
                }
            }
        }

        result.Sort(StringComparer.Ordinal);
        return result;
    }
}
