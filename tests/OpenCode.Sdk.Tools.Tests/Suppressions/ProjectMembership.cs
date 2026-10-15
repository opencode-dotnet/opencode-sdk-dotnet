namespace OpenCode.Sdk.Tools.Tests.Suppressions;

/// <summary>
/// The C# files a project-wide warning list can reach, over-approximated on purpose: every C# file
/// below the project's directory (a nested project's files included, as the SDK's default compile
/// glob includes them), plus every file a <c>&lt;Compile Include&gt;</c> item names, a wildcard
/// include standing for every C# file below its fixed folder. Removals and conditions are not
/// evaluated. An over-wide reach can only report an overlap that is not there, loudly; it never
/// hides one.
/// </summary>
internal sealed class ProjectMembership
{
    private const string SourceExtension = ".cs";

    private readonly IReadOnlyList<string> _sources;
    private readonly IReadOnlyDictionary<string, IReadOnlyList<string>> _compileIncludes;

    public ProjectMembership(IReadOnlyList<string> files, IReadOnlyDictionary<string, IReadOnlyList<string>> compileIncludes)
    {
        ArgumentNullException.ThrowIfNull(files);
        ArgumentNullException.ThrowIfNull(compileIncludes);

        _sources = [.. files.Where(static file => file.EndsWith(SourceExtension, StringComparison.Ordinal))];
        _compileIncludes = compileIncludes;
    }

    /// <summary>Every C# file below <paramref name="directory"/> (the whole repository for an empty one).</summary>
    public IEnumerable<string> FilesUnder(string directory) =>
        _sources.Where(file => IsUnder(DirectoryOf(file), directory));

    /// <summary>The C# files one project can compile.</summary>
    public IEnumerable<string> FilesOf(string project)
    {
        var includes = _compileIncludes.TryGetValue(project, out var found)
            ? found
            : throw new InvalidOperationException($"'{project}' is not a project in the repository.");
        return FilesUnder(DirectoryOf(project))
            .Concat(includes.SelectMany(include => Resolve(DirectoryOf(project), include)))
            .Distinct(StringComparer.Ordinal);
    }

    internal static string DirectoryOf(string path)
    {
        var separator = path.LastIndexOf('/', StringComparison.Ordinal);
        return separator < 0 ? string.Empty : path[..separator];
    }

    private static bool IsUnder(string path, string directory) =>
        directory.Length == 0 || string.Equals(path, directory, StringComparison.Ordinal)
        || path.StartsWith(directory + "/", StringComparison.Ordinal);

    private IEnumerable<string> Resolve(string projectDirectory, string include)
    {
        var path = Normalize(projectDirectory, include.Replace('\\', '/'));
        var wildcard = path.IndexOfAny(['*', '?']);
        return wildcard < 0 ? [path] : FilesUnder(DirectoryOf(path[..wildcard]));
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
