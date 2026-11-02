using System.IO.Abstractions;
using System.Text.RegularExpressions;
using System.Xml.Linq;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

internal sealed class PlaygroundGuard(IFileSystem fileSystem)
{
    public async Task<IReadOnlyList<string>> FindAsync(string root)
    {
        var playground = fileSystem.Path.Combine(root, "playground");
        var solution = XDocument.Parse(await fileSystem.File.ReadAllTextAsync(fileSystem.Path.Combine(root, "OpenCode.slnx")));
        var projects = solution.Descendants("Project").Attributes("Path")
            .Select(static path => path.Value.Replace('\\', '/')).ToHashSet(StringComparer.Ordinal);
        var index = await fileSystem.File.ReadAllTextAsync(fileSystem.Path.Combine(playground, "README.md"));
        var violations = new List<string>();
        foreach (var project in fileSystem.Directory.EnumerateFiles(playground, "*.csproj", SearchOption.AllDirectories))
        {
            var path = fileSystem.Path.GetRelativePath(root, project).Replace('\\', '/');
            if (!projects.Contains(path))
            {
                violations.Add($"{path}: missing Project Path in OpenCode.slnx.");
            }
        }

        foreach (var folder in fileSystem.Directory.EnumerateDirectories(playground))
        {
            if (!fileSystem.Directory.EnumerateFiles(folder, "*.csproj", SearchOption.AllDirectories).Any())
            {
                continue;
            }

            var name = fileSystem.Path.GetFileName(folder);
            if (!fileSystem.File.Exists(fileSystem.Path.Combine(folder, "README.md")))
            {
                violations.Add($"playground/{name}/README.md: missing README.");
            }

            if (!Regex.IsMatch(index, @"(?:^|[^!])\[[^\]\r\n]*\]\((?:\./)?" + Regex.Escape(name) + @"(?:/[^)\s]*)?\)",
                RegexOptions.NonBacktracking, Regex.InfiniteMatchTimeout))
            {
                violations.Add($"playground/{name}: missing inline link in playground/README.md.");
            }
        }

        return [.. violations.Order(StringComparer.Ordinal)];
    }
}
