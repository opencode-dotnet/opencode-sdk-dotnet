using System.IO.Abstractions;
using System.Text.RegularExpressions;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;

internal sealed class CiWorkflow(IFileSystem fileSystem)
{
    public async Task<string> ReadAsync(string root) =>
        (await fileSystem.File.ReadAllTextAsync(fileSystem.Path.Combine(root, ".github", "workflows", "ci.yml")))
            .ReplaceLineEndings("\n");

    public static string Job(string workflow, string name) =>
        Regex.Match(workflow, $@"(?ms)^  {Regex.Escape(name)}:\n.*?(?=^  \S|\z)", RegexOptions.None, Regex.InfiniteMatchTimeout).Value;
}
