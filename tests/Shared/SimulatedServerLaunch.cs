using System.IO.Abstractions;

namespace OpenCode.Sdk.TestSupport;

/// <summary>
/// The launch recipe of the persistent simulation host: its command, the CLI-package working
/// directory bun needs to resolve the monorepo workspace, the isolated environment with the
/// simulation switches and the per-run drive manifest, and that manifest. The caller holds
/// the <see cref="MachineLock.DrivePorts"/> lock from <see cref="Prepare"/> until the server is
/// ready, because the manifest names the ports the host binds later.
/// </summary>
internal sealed record SimulatedServerLaunch
{
    public required IReadOnlyList<string> Command { get; init; }

    public required string WorkingDirectory { get; init; }

    public required IReadOnlyDictionary<string, string> Environment { get; init; }

    public required DriveManifest Manifest { get; init; }

    /// <summary>Gets the isolation boundary the host is launched inside and must be seen to honour.</summary>
    public required IsolationBoundary Isolation { get; init; }

    /// <summary>
    /// Bounded well above the realistic worst case - every local target-framework leg starting a
    /// simulated server back to back, each within the readiness bound - so a genuinely wedged
    /// gate holder still fails loudly instead of hanging the suite.
    /// </summary>
    public static TimeSpan GateTimeout { get; } = TimeSpan.FromMinutes(15);

    /// <summary>The launcher options for this recipe under <see cref="OwnedServerPolicy"/>, retaining output in <paramref name="output"/>.</summary>
    public OpenCodeServerOptions Options(OpenCodeServerOutput output)
    {
        ArgumentNullException.ThrowIfNull(output);

        return new OpenCodeServerOptions
        {
            Command = Command,
            WorkingDirectory = WorkingDirectory,
            Environment = Environment,
            ReadinessTimeout = OwnedServerPolicy.ReadinessTimeout,
            GracefulShutdownTimeout = OwnedServerPolicy.GracefulShutdownTimeout,
            Output = output,
        };
    }

    /// <summary>
    /// Starts the host and confirms, before anything reaches it, that it opened its state inside
    /// the isolated run root; a host that did not is ended rather than handed out.
    /// </summary>
    /// <param name="output">The collector the host's output is retained in.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The started host.</returns>
    public async Task<OpenCodeServer> StartAsync(OpenCodeServerOutput output, CancellationToken cancellationToken)
    {
        var server = await OpenCodeServer.StartAsync(Options(output), cancellationToken).ConfigureAwait(false);
        try
        {
            Isolation.ConfirmHonored("The simulated server");
            return server;
        }
        catch (InvalidOperationException)
        {
            await server.DisposeAsync().ConfigureAwait(false);
            throw;
        }
    }

    public static SimulatedServerLaunch Prepare(IFileSystem fileSystem, TestRunRoot runRoot)
    {
        ArgumentNullException.ThrowIfNull(fileSystem);
        ArgumentNullException.ThrowIfNull(runRoot);

        var registry = runRoot.CreateSubdirectory("drive");
        var persistentHost = new PersistentSimulationServerCommand(fileSystem);
        var manifest = DriveManifest.Write(fileSystem, registry);
        var isolation = ServerIsolation.For(fileSystem, runRoot.Path);
        var environment = isolation.Environment;
        environment["OPENCODE_SIMULATE"] = "1";
        environment["OPENCODE_DRIVE"] = manifest.InstanceName;
        environment["DRIVE_REGISTRY_DIR"] = registry;
        environment["OPENCODE_CONFIG_CONTENT"] = SimulationConfigSeed.Json;
        environment["OPENCODE_LOG_LEVEL"] = "INFO";
        environment["OPENCODE_PRINT_LOGS"] = "1";
        return new SimulatedServerLaunch
        {
            Command = persistentHost.Resolve(),
            WorkingDirectory = persistentHost.WorkingDirectory,
            Environment = environment,
            Manifest = manifest,
            Isolation = isolation,
        };
    }
}
