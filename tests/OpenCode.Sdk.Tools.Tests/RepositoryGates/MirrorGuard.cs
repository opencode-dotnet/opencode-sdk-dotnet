using OpenCode.Sdk.Tools.Tests.RepositoryGates.Support;

namespace OpenCode.Sdk.Tools.Tests.RepositoryGates;

/// <summary>
/// An XTests.cs in Tools.Tests or Sdk.Tests whose subject has exactly one Internal-dropped source
/// folder in the paired tools or SDK project, and whose own folder differs from that folder.
/// </summary>
internal static class MirrorGuard
{
    public const string Rule = "Mirror";
    public const string Text = "An XTests.cs whose unique subject folder in the paired project, with Internal segments dropped, differs from its own folder.";

    public static IReadOnlyList<RepositoryGateSite> Find(TestSourceCorpus corpus)
    {
        var sites = new List<RepositoryGateSite>();
        (string Tests, string Source)[] pairs =
        [
            ("tests/OpenCode.Sdk.Tools.Tests/", "tools/OpenCode.Sdk.Tools/"),
            ("tests/OpenCode.Sdk.Tests/", "src/OpenCode.Sdk/"),
        ];
        foreach (var (tests, source) in pairs)
        {
            var subjects = corpus.Paths.Where(path => path.StartsWith(source, StringComparison.Ordinal) && path.EndsWith(".cs", StringComparison.Ordinal))
                .Select(path => path[source.Length..])
                .GroupBy(static path => path[(path.LastIndexOf('/', StringComparison.Ordinal) + 1)..].Split('.')[0], StringComparer.Ordinal)
                .ToDictionary(static group => group.Key,
                    static group => group.Select(static path => string.Join('/', Folder(path).Split('/').Where(static part => part != "Internal")))
                        .Distinct(StringComparer.Ordinal).ToArray(), StringComparer.Ordinal);
            foreach (var path in corpus.Paths.Where(path => path.StartsWith(tests, StringComparison.Ordinal) && path.EndsWith("Tests.cs", StringComparison.Ordinal)))
            {
                var relative = path[tests.Length..];
                var subject = relative[(relative.LastIndexOf('/', StringComparison.Ordinal) + 1)..^"Tests.cs".Length];
                if (subjects.TryGetValue(subject, out var homes) && homes.Length == 1 && !string.Equals(homes[0], Folder(relative), StringComparison.Ordinal))
                {
                    sites.Add(new RepositoryGateSite(path, 1, 0, Rule, $"{Text} Expected folder '{homes[0]}'."));
                }
            }
        }

        return sites;
    }

    private static string Folder(string relative)
    {
        var slash = relative.LastIndexOf('/', StringComparison.Ordinal);
        return slash < 0 ? string.Empty : relative[..slash];
    }
}
