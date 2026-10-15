namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// The C# files each project compiles, as far as a project-wide warning list needs them: the files
/// under the project's directory that no nearer project owns, and the files its
/// <c>&lt;Compile Include&gt;</c> items link, either by path or as <c>folder/**/*.cs</c>. Any other
/// include shape is refused, because a file the membership missed would hide an overlap.
/// </summary>
internal sealed class ProjectMembership
{
    private const string ProjectExtension = ".csproj";
    private const string SourceExtension = ".cs";
    private const string RecursiveSources = "/**/*.cs";

    private readonly Dictionary<string, List<string>> _files = new(StringComparer.Ordinal);

    public ProjectMembership(IReadOnlyList<string> files, IReadOnlyDictionary<string, IReadOnlyList<string>> compileIncludes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(compileIncludes);

        var projects = files.Where(static file => file.EndsWith(ProjectExtension, StringComparison.Ordinal)).ToList();
        foreach (var project in projects)
        {
            _files.Add(project, []);
        }

        var directories = projects.ToDictionary(DirectoryOf, static project => project, StringComparer.Ordinal);
        foreach (var file in files.Where(static file => file.EndsWith(SourceExtension, StringComparison.Ordinal)))
        {
            for (var directory = DirectoryOf(file); directory is not null; directory = Parent(directory))
            {
                if (directories.TryGetValue(directory, out var owner))
                {
                    _files[owner].Add(file);
                    break;
                }
            }
        }

        foreach (var (project, includes) in compileIncludes)
        {
            foreach (var include in includes)
            {
                _files[project].AddRange(Resolve(project, include, files));
            }
        }
    }

    /// <summary>The C# files the projects at or below <paramref name="directory"/> compile.</summary>
    public IEnumerable<string> FilesUnder(string directory) =>
        _files.Where(pair => IsUnder(DirectoryOf(pair.Key), directory)).SelectMany(static pair => pair.Value).Distinct(StringComparer.Ordinal);

    /// <summary>The C# files one project compiles.</summary>
    public IReadOnlyList<string> FilesOf(string project) =>
        _files.TryGetValue(project, out var files) ? files : throw new InvalidOperationException($"'{project}' is not a project in the repository.");

    internal static string DirectoryOf(string path)
    {
        var separator = path.LastIndexOf('/', StringComparison.Ordinal);
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static string? Parent(string directory) => directory.Length == 0 ? null : DirectoryOf(directory);

    private static bool IsUnder(string path, string directory) =>
        directory.Length == 0 || string.Equals(path, directory, StringComparison.Ordinal)
        || path.StartsWith(directory + "/", StringComparison.Ordinal);

    private static IEnumerable<string> Resolve(string project, string include, IReadOnlyList<string> files)
    {
        var path = Normalize(DirectoryOf(project), include.Replace('\\', '/'));
        if (path.EndsWith(RecursiveSources, StringComparison.Ordinal) && !path[..^RecursiveSources.Length].Contains('*', StringComparison.Ordinal))
        {
            var folder = path[..^RecursiveSources.Length];
            return files.Where(file => file.EndsWith(SourceExtension, StringComparison.Ordinal) && IsUnder(DirectoryOf(file), folder));
        }

        return path.Contains('*', StringComparison.Ordinal)
            ? throw new InvalidOperationException($"{project} compiles '{include}'; the suppression ledger resolves only a path or 'folder/**/*.cs'.")
            : [path];
    }

    private static string Normalize(string directory, string relative)
    {
        var parts = new List<string>(directory.Length == 0 ? [] : directory.Split('/'));
        foreach (var part in relative.Split('/'))
        {
            if (part == "..")
            {
                parts.RemoveAt(parts.Count - 1);
            }
            else if (part is not ("." or ""))
            {
                parts.Add(part);
            }
        }

        return string.Join('/', parts);
    }
}
