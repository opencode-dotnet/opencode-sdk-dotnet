using System.IO.Abstractions;
using System.Text;
using OpenCode.Sdk.Tools.Generator.Refresh.Abstractions;

namespace OpenCode.Sdk.Tools.Tests.Support;

/// <summary>
/// The files git tracks, read from the working tree rather than the index contents. Untracked files are
/// never read: a test run writes its results into the tree, and another process may hold them open.
/// </summary>
internal sealed class RepositoryFileSet(IFileSystem fileSystem)
{
    private readonly IFileSystem _fileSystem = fileSystem ?? throw new ArgumentNullException(nameof(fileSystem));

    public async Task<IReadOnlyList<string>> EnumerateAsync(string root, IProcessRunner runner, CancellationToken cancellationToken)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(root);

        var result = await runner.RunAsync("git", ["ls-files", "-z", "--cached"], root, cancellationToken);
        if (result.ExitCode != 0)
        {
            throw new InvalidOperationException("git ls-files failed: " + result.StandardError);
        }

        return [.. Encoding.UTF8.GetString(result.StandardOutput).Split('\0', StringSplitOptions.RemoveEmptyEntries)
            .Select(static path => path.Replace('\\', '/'))
            .Distinct(StringComparer.Ordinal)
            .Where(path => _fileSystem.File.Exists(_fileSystem.Path.Combine(root, path)))
            .Order(StringComparer.Ordinal)];
    }
}
