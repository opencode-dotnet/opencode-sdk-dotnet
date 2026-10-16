using OpenCode.Sdk.Tools.Tests.Support;
using Testably.Abstractions.Testing;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;

internal sealed class RepositoryGateScenario
{
    private readonly Dictionary<string, string> _files = new(StringComparer.Ordinal);

    public MockFileSystem FileSystem { get; } = new();

    public string Root => FileSystem.Path.GetFullPath("repo");

    public ScriptedProcessRunner Runner { get; } = new();

    public RepositoryGateScenario WithFile(string path, string text = "")
    {
        _files[path] = text;
        return this;
    }

    public async Task BuildAsync()
    {
        foreach (var (path, text) in _files)
        {
            var fullPath = FileSystem.Path.Combine(Root, path);
            FileSystem.Directory.CreateDirectory(FileSystem.Path.GetDirectoryName(fullPath)!);
            await FileSystem.File.WriteAllTextAsync(fullPath, text);
        }
    }

    public RepositoryGateScenario WithGitFiles(params string[] paths)
    {
        Runner.Expect("git", "ls-files -z --cached --others --exclude-standard", ScriptedProcessRunner.Ok(string.Join('\0', paths) + "\0"));
        return this;
    }

    public async Task<TestSourceCorpus> CorpusAsync()
    {
        await BuildAsync();
        return await new TestSourceCorpusReader(FileSystem).ReadAsync(Root, [.. _files.Keys.Order(StringComparer.Ordinal)], CancellationToken.None);
    }

    public RepositoryGateScenario WithPhysicalLines(string path, int count, string newline, bool finalNewline) =>
        WithFile(path, string.Join(newline, Enumerable.Repeat("// Assert.That(xs.Any()).IsTrue();", count)) + (finalNewline ? newline : string.Empty));
}
