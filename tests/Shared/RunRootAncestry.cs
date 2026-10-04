using System.IO.Abstractions;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// What the pinned server would load into every owned server from above a run root. Configuration
/// discovery walks from each location to the drive root with no stop
/// (<c>packages/core/src/config/discovery.ts:36</c> over <c>packages/util/src/fs-util.ts:164-187</c>
/// at the pin), so an ancestor's project configuration, and the skills directories it watches
/// natively, belong to every workspace beneath it - whatever the environment redirects. Project
/// resolution walks the same way: a workspace inside a repository belongs to that repository's
/// project, so an enclosing checkout would become the project of every plain workspace.
/// </summary>
internal sealed class RunRootAncestry
{
    /// <summary>
    /// The project configuration files discovery reads (<c>discovery.ts:11</c>), and the marker of
    /// a linked git worktree, which is a file where a repository has a directory.
    /// </summary>
    private static readonly string[] StateFiles = ["opencode.json", "opencode.jsonc", ".git"];

    /// <summary>
    /// A repository, the project root discovery loads and watches as a directory, and the two
    /// compatibility roots, which contribute their skills directory and nothing else
    /// (<c>discovery.ts:41</c>, <c>config/plugin/compatibility.ts:41</c>).
    /// </summary>
    private static readonly string[][] StateDirectories =
        [[".git"], [".opencode"], [".claude", "skills"], [".agents", "skills"]];

    private readonly IFileSystem _fileSystem;

    public RunRootAncestry(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        _fileSystem = fileSystem;
    }

    /// <summary>Finds the nearest state an ancestor of <paramref name="directory"/>, or the directory itself, would contribute.</summary>
    /// <param name="directory">The directory run roots are created in.</param>
    /// <returns>The offending path, or null when the whole chain is clean.</returns>
    public string? FindDeveloperState(string directory)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(directory);

        for (var current = _fileSystem.Path.GetFullPath(directory);
             current is { Length: > 0 };
             current = _fileSystem.Path.GetDirectoryName(current))
        {
            if (StateIn(current) is { } state)
            {
                return state;
            }
        }

        return null;
    }

    private string? StateIn(string directory)
    {
        foreach (var segments in StateDirectories)
        {
            var candidate = _fileSystem.Path.Combine([directory, .. segments]);
            if (_fileSystem.Directory.Exists(candidate))
            {
                return candidate;
            }
        }

        foreach (var file in StateFiles)
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
