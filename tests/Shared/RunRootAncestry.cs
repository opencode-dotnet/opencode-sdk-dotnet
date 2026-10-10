using System.IO.Abstractions;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// What the pinned server would load from above a location when its project configuration
/// discovery is on. Discovery walks from the location to the drive root with no stop
/// (<c>packages/core/src/config/discovery.ts:33-36</c> over <c>packages/util/src/fs-util.ts:165</c>
/// at the pin), so an ancestor's project configuration belongs to every workspace beneath it -
/// whatever the environment redirects. Every owned server turns that walk off
/// (<see cref="ServerIsolation"/>); these checks guard what still walks: the server whose subject
/// is project configuration, and an installed command that may not read the switch. Project
/// resolution walks the same way and has no switch: a workspace inside a repository belongs to
/// that repository's project, so an enclosing checkout would become the project of every plain
/// workspace.
/// </summary>
internal sealed class RunRootAncestry
{
    /// <summary>A repository, and the marker of a linked git worktree, which is a file where a repository has a directory.</summary>
    private const string Repository = ".git";

    /// <summary>The project configuration files discovery reads (<c>discovery.ts:11</c>).</summary>
    private static readonly string[] ConfigurationFiles = ["opencode.json", "opencode.jsonc"];

    /// <summary>
    /// The project directory, which can carry MCP servers, plugins, tools, agents, commands and
    /// skills, as the two configuration files can.
    /// </summary>
    private static readonly string[][] ConfigurationDirectories = [[".opencode"]];

    /// <summary>
    /// The skills directories of the two compatibility roots discovery registers in every
    /// directory it walks (<c>discovery.ts:41</c>, <c>config/plugin/compatibility.ts:39</c>). Their
    /// skills are developer state a server loads and hands the model, like any other project
    /// configuration; unlike it, they start, load and authenticate nothing.
    /// </summary>
    private static readonly string[][] SkillDirectories = [[".claude", "skills"], [".agents", "skills"]];

    private readonly IFileSystem _fileSystem;

    public RunRootAncestry(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        _fileSystem = fileSystem;
    }

    /// <summary>
    /// Finds the nearest state an ancestor of <paramref name="directory"/>, or the directory
    /// itself, would contribute to a workspace beneath it: a repository, and every kind of project
    /// configuration discovery loads, skills included. The suite chooses where run roots live, so
    /// nothing of the kind is accepted above them.
    /// </summary>
    /// <param name="directory">The directory run roots are created in.</param>
    /// <returns>The offending path, or null when the whole chain is clean.</returns>
    public string? FindDeveloperState(string directory) =>
        Walk(directory, [[Repository], .. ConfigurationDirectories, .. SkillDirectories], [.. ConfigurationFiles, Repository]);

    /// <summary>
    /// Finds the nearest project configuration in <paramref name="directory"/> or above it that
    /// can start MCP servers, load plugins, or add tools in a server whose location is beneath it.
    /// The developer chooses where the checkout lives, and only a server with discovery on walks
    /// from there for a request without a location, so this refuses what would run inside it.
    /// Skills directories are the same developer state <see cref="FindDeveloperState"/> refuses,
    /// and stay accepted here: a checkout under a home that keeps its own skills is the usual
    /// Linux and macOS layout, and such a server would read them but run nothing from them.
    /// </summary>
    /// <param name="directory">The first directory, walking up, that the checkout does not own.</param>
    /// <returns>The offending path, or null when the whole chain is clean.</returns>
    public string? FindProjectConfiguration(string directory) =>
        Walk(directory, ConfigurationDirectories, ConfigurationFiles);

    private string? Walk(string directory, string[][] directories, string[] files)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        for (var current = _fileSystem.Path.GetFullPath(directory);
             current is { Length: > 0 };
             current = _fileSystem.Path.GetDirectoryName(current))
        {
            if (StateIn(current, directories, files) is { } state)
            {
                return state;
            }
        }

        return null;
    }

    private string? StateIn(string directory, string[][] directories, string[] files)
    {
        foreach (var segments in directories)
        {
            var candidate = _fileSystem.Path.Combine([directory, .. segments]);
            if (_fileSystem.Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var file in files)
        {
            var candidate = _fileSystem.Path.Combine(directory, file);
            if (_fileSystem.File.Exists(candidate))
            {
                return candidate;
            }
        }

        return null;
    }
}
