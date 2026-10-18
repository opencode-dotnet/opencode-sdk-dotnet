using System.Globalization;
using System.IO.Abstractions;
using System.Text.RegularExpressions;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

/// <summary>Protects consumer-facing pin mentions from going stale when spec/SNAPSHOT.md's pinned version changes.</summary>
internal sealed partial class ConsumerPinGuard(IFileSystem fileSystem)
{
    private const string Version = @"[0-9]+\.[0-9]+\.[0-9]+(?:-[0-9A-Za-z.-]+)?(?:\+[0-9A-Za-z.-]+)?";

    public async Task<IReadOnlyList<string>> FindAsync(string root)
    {
        var snapshot = await fileSystem.File.ReadAllTextAsync(fileSystem.Path.Combine(root, "spec/SNAPSHOT.md"));
        var tags = ReleaseTag().Matches(snapshot);
        if (tags.Count != 1)
        {
            return [string.Create(CultureInfo.InvariantCulture,
                $"spec/SNAPSHOT.md: expected exactly one '| Upstream release tag | `vX.Y.Z` |' row; found {tags.Count} matches.")];
        }

        var expected = tags[0].Groups["version"].Value;
        var paths = new[] { fileSystem.Path.Combine(root, "README.md") }
            .Concat(fileSystem.Directory.EnumerateFiles(fileSystem.Path.Combine(root, "docs/guide"), "*.md", SearchOption.AllDirectories))
            .Concat(fileSystem.Directory.EnumerateFiles(fileSystem.Path.Combine(root, ".github/ISSUE_TEMPLATE"), "*", SearchOption.AllDirectories))
            .Select(path => fileSystem.Path.GetRelativePath(root, path).Replace('\\', '/'))
            .Order(StringComparer.Ordinal);
        var stale = new List<string>();
        foreach (var path in paths)
        {
            var lines = await fileSystem.File.ReadAllLinesAsync(fileSystem.Path.Combine(root, path));
            var serverVersion = false;
            for (var index = 0; index < lines.Length; index++)
            {
                var line = lines[index].Trim();
                if (line.StartsWith("id:", StringComparison.Ordinal))
                {
                    serverVersion = line is "id: server-version" or "id: \"server-version\"" or "id: 'server-version'";
                }

                foreach (var groups in PinMention().Matches(lines[index]).Select(static match => match.Groups))
                {
                    if (groups["placeholder"].Success &&
                        (!serverVersion || !path.StartsWith(".github/ISSUE_TEMPLATE/", StringComparison.Ordinal)))
                    {
                        continue;
                    }

                    var version = groups["version"].Value;
                    if (!string.Equals(version, expected, StringComparison.Ordinal))
                    {
                        stale.Add(string.Create(CultureInfo.InvariantCulture,
                            $"{path}:{index + 1}: consumer pin {version}; expected {expected}."));
                    }
                }
            }
        }

        return stale;
    }

    [GeneratedRegex(@"^\| Upstream release tag \| `v(?<version>" + Version + @")` \|\r?$",
        RegexOptions.Multiline, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex ReleaseTag();

    [GeneratedRegex("""(?:\bnpm install -g (?<quote>["'])?@opencode/cli@|at the pin \(`v|(?<placeholder>^\s*placeholder:\s*)(?<quote>["'])?)(?<version>""" + Version + @")(?(quote)\k<quote>)",
        RegexOptions.None, matchTimeoutMilliseconds: Timeout.Infinite)]
    private static partial Regex PinMention();
}
