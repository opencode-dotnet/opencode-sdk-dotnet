using System.IO.Abstractions;
using Microsoft.CodeAnalysis.Text;
using OpenCode.Sdk.Tools.Tests.Support;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;

internal sealed class TestSourceCorpusReader(IFileSystem fileSystem)
{
    public async Task<TestSourceCorpus> ReadAsync(string root, IReadOnlyList<string> paths, CancellationToken cancellationToken)
    {
        var files = new List<CSharpSourceFile>();
        foreach (var path in paths.Where(static path => path.StartsWith("tests/", StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal)))
        {
            var text = SourceText.From(await fileSystem.File.ReadAllTextAsync(fileSystem.Path.Combine(root, path), cancellationToken));
            files.Add(new CSharpSourceFile(path, text, CSharpSourceReader.Read(text)));
        }

        return new TestSourceCorpus([.. paths], files.AsReadOnly());
    }
}
