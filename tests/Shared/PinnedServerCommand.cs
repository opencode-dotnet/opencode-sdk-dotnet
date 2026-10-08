using System.IO.Abstractions;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// Locates the pinned server-under-test: bun running the submodule's CLI entry from source, the
/// only build in which the simulation package exists. Fail-fast by design (ADR-0022): a missing
/// submodule or dependency install is an instructive error, never a skip.
/// </summary>
internal sealed class PinnedServerCommand
{
    private readonly IFileSystem _fileSystem;
    private readonly Lazy<string> _repositoryRoot;

    public PinnedServerCommand(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        _fileSystem = fileSystem;
        _repositoryRoot = new Lazy<string>(FindRepositoryRoot);
    }

    public string RepositoryRoot => _repositoryRoot.Value;

    /// <summary>Resolves the command that runs the pinned server from source.</summary>
    /// <returns>bun, the CLI entry, and <c>serve</c>.</returns>
    /// <exception cref="InvalidOperationException">
    /// The checkout or its install is missing, or this session could not scrub the provider credentials a server would inherit.
    /// </exception>
    public IReadOnlyList<string> Resolve()
    {
        InheritedEnvironment.RequireProviderCredentialsScrubbed();
        var submodule = _fileSystem.Path.Combine(RepositoryRoot, "external", "opencode");
        var entry = _fileSystem.Path.Combine(submodule, "packages", "cli", "src", "index.ts");
        if (!_fileSystem.File.Exists(entry))
        {
            throw new InvalidOperationException(
                $"The pinned server source is missing at '{entry}'. Run: git submodule update --init external/opencode");
        }

        if (!_fileSystem.Directory.Exists(_fileSystem.Path.Combine(submodule, "node_modules")))
        {
            throw new InvalidOperationException(
                $"The pinned server dependencies are not installed under '{submodule}'. Run there: bun install --frozen-lockfile --ignore-scripts");
        }

        return ["bun", entry, "serve"];
    }

    /// <summary>
    /// Gets the directory a pinned server that is not in service mode starts in. Bun resolves the
    /// pinned monorepo's workspace and tsconfig from the process's working directory, not from the
    /// absolute entry path: a directory outside the checkout fails the source run before readiness
    /// with "Cannot find module 'react/jsx-dev-runtime'". So the server starts in the CLI package,
    /// as upstream's own dev script does. That directory is also where the server resolves a
    /// request that names no location, and configuration discovery walks from a location to the
    /// drive root: from the CLI package through <c>packages</c> and the upstream checkout, then
    /// through this repository's <c>external</c> directory, the repository root, and every
    /// directory above it. The upstream checkout's own project configuration is pinned; from
    /// <c>external</c> upward it is the developer's, and its MCP servers and plugins would start
    /// inside every owned server. A checkout with any of it there refuses to launch. Every owned
    /// server is located through <see cref="Resolve"/>, this directory, or both - an installed
    /// command an override names still starts here - so both are where a session that could not
    /// scrub the provider credentials stops.
    /// </summary>
    /// <exception cref="InvalidOperationException">
    /// Project configuration sits in the checkout's <c>external</c> directory or above it, or this
    /// session could not scrub the provider credentials a server would inherit.
    /// </exception>
    public string WorkingDirectory
    {
        get
        {
            InheritedEnvironment.RequireProviderCredentialsScrubbed();
            var external = _fileSystem.Path.Combine(RepositoryRoot, "external");
            var directory = _fileSystem.Path.Combine(external, "opencode", "packages", "cli");
            if (new RunRootAncestry(_fileSystem).FindProjectConfiguration(external) is { } configuration)
            {
                throw new InvalidOperationException(
                    $"The pinned server starts in '{directory}', and '{configuration}' above it is project "
                    + "configuration the server would load: its MCP servers, plugins and tools would start in "
                    + "every owned server. Move it, or the checkout, so that nothing of the kind is in '"
                    + external + "' or above it.");
            }

            return directory;
        }
    }

    private string FindRepositoryRoot()
    {
        // The pattern (rather than a separate `!string.IsNullOrEmpty` call) is what narrows
        // `directory` to non-null on every TFM: net472's older BCL surface does not carry the
        // `NotNullWhen` attribute `IsNullOrEmpty` relies on for flow analysis elsewhere, which
        // would otherwise leave this a CS8604 on that leg only.
        var directory = AppContext.BaseDirectory;
        while (directory is { Length: > 0 })
        {
            if (_fileSystem.File.Exists(_fileSystem.Path.Combine(directory, "OpenCode.slnx")))
            {
                return directory;
            }

            directory = _fileSystem.Path.GetDirectoryName(directory);
        }

        throw new InvalidOperationException(
            "Could not locate the repository root (OpenCode.slnx) above the test base directory.");
    }
}
