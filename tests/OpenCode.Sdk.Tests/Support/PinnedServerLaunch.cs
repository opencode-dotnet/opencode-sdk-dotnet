using System.IO.Abstractions;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The one launch arrangement that actually boots the pinned source server, so every launcher
/// test states only its own variation instead of repeating the arrangement.
/// </summary>
internal sealed class PinnedServerLaunch
{
    private readonly IFileSystem _fileSystem;
    private readonly PinnedServerCommand _command;

    public PinnedServerLaunch(IFileSystem fileSystem)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);

        _fileSystem = fileSystem;
        _command = new PinnedServerCommand(fileSystem);
    }

    /// <summary>Gets bun running the submodule's CLI entry: the executable and its leading arguments.</summary>
    public IReadOnlyList<string> Command => _command.Resolve();

    /// <summary>
    /// Starts the pinned server over its own run root and hands it out only once it has opened its
    /// state there: a server that did not is ended rather than returned.
    /// </summary>
    /// <param name="runRoot">The per-run root every global state directory is redirected into.</param>
    /// <param name="command">A stand-in command that still ends in the pinned server; null launches the pinned one directly.</param>
    /// <param name="gracefulShutdownTimeout">The launcher's graceful shutdown bound; null keeps its default.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The started server.</returns>
    public async Task<OpenCodeServer> StartAsync(
        TestRunRoot runRoot,
        IReadOnlyList<string>? command = null,
        TimeSpan? gracefulShutdownTimeout = null,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(runRoot);

        return await StartAsync(ServerIsolation.For(_fileSystem, runRoot.Path), command, gracefulShutdownTimeout, cancellationToken)
            .ConfigureAwait(false);
    }

    /// <summary>
    /// Starts the pinned server over its own run root with project configuration discovery on, for
    /// a test whose subject is project configuration; every request it sends names a location
    /// inside <paramref name="runRoot"/>.
    /// </summary>
    /// <param name="runRoot">The per-run root every global state directory is redirected into.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The started server.</returns>
    public async Task<OpenCodeServer> StartWithProjectConfigurationAsync(TestRunRoot runRoot, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(runRoot);

        return await StartAsync(ServerIsolation.ForProjectConfiguration(_fileSystem, runRoot.Path), null, null, cancellationToken)
            .ConfigureAwait(false);
    }

    private async Task<OpenCodeServer> StartAsync(
        IsolationBoundary isolation,
        IReadOnlyList<string>? command,
        TimeSpan? gracefulShutdownTimeout,
        CancellationToken cancellationToken)
    {
        var options = new OpenCodeServerOptions
        {
            Command = command ?? Command,
            WorkingDirectory = _command.WorkingDirectory,
            Environment = isolation.Environment,
            ReadinessTimeout = TimeSpan.FromMinutes(3),
        };
        if (gracefulShutdownTimeout is { } grace)
        {
            options.GracefulShutdownTimeout = grace;
        }

        var server = await OpenCodeServer.StartAsync(options, cancellationToken).ConfigureAwait(false);
        try
        {
            isolation.ConfirmHonored("The pinned launcher test's server");
            return server;
        }
        catch (Exception)
        {
            // Whatever the check threw, the caller never receives the server, so nothing else would end it.
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }
}
