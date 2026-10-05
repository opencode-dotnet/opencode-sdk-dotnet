using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Discovery;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.Internal.BackgroundService.Stop;

namespace OpenCode.Sdk.Internal.BackgroundService.Ensure;

/// <summary>
/// The Ensure door behind <c>OpenCodeServer.EnsureAsync</c>, the CLI's managed-service resolution
/// (<c>resolveManaged</c> in <c>server-connection.ts</c>) over the pinned client's <c>ensure</c>: the options are
/// validated and copied before any I/O, the registration is located once, the <c>Error</c> policy
/// runs its two Discover calls, and a <see cref="ServiceElection"/> does the rest.
/// </summary>
internal sealed class ServiceEnsurer(
    IServiceEnvironment environment,
    IServiceFileSystem fileSystem,
    IServiceInfoProbe probe,
    IServiceContenderSpawner spawner,
    IServicePtyHandoff handoff,
    IServiceProcessControl processControl,
    IServiceClock clock,
    ExecutableResolver executableResolver,
    ServiceTiming timing)
{
    internal const string TimeoutMessage = "Timed out waiting for the background service to start.";
    internal const string FailedMessage = "The background service failed to start.";
    internal const string MismatchMessage = "The background server version does not match this client.";
    internal const string IncompatibleProtocolMessage =
        "The background service uses an incompatible health protocol. Update this client or explicitly restart the service.";

    /// <summary>Ensures a healthy compatible service is running and returns its registration.</summary>
    /// <param name="options">The caller's options; null means every default.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The ready service's registration, which always carries a password.</returns>
    /// <exception cref="ArgumentException">The options are blank, contradictory, or missing a value their policy needs.</exception>
    /// <exception cref="ArgumentOutOfRangeException">The version policy is not a defined value.</exception>
    /// <exception cref="OpenCodeServerException">No user home resolves, the election timed out, the service failed, its health protocol is incompatible, a contender failed, or a version mismatch was refused.</exception>
    public async Task<ServiceRegistration> EnsureAsync(OpenCodeServerEnsureOptions? options, CancellationToken cancellationToken)
    {
        var request = EnsureRequest.Snapshot(options);
        cancellationToken.ThrowIfCancellationRequested();

        var paths = await new ServiceRegistrationLocator(environment, fileSystem)
            .LocateAsync(request.Selection, cancellationToken)
            .ConfigureAwait(false);

        if (request.Policy == OpenCodeServerVersionPolicy.Error)
        {
            // resolveManaged: a service at the expected version is reused, a service at
            // any other version is refused, and nothing registered enters the election.
            var discovery = new ServiceDiscovery(environment, fileSystem, probe);
            if (await discovery.DiscoverAtAsync(paths, request.Selection.ExpectedVersion, cancellationToken).ConfigureAwait(false) is { } compatible)
            {
                return compatible;
            }

            if (await discovery.DiscoverAtAsync(paths, expectedVersion: null, cancellationToken).ConfigureAwait(false) is not null)
            {
                throw new OpenCodeServerException(MismatchMessage);
            }
        }

        var seams = new ServiceElectionSeams(
            probe,
            spawner,
            handoff,
            new ServiceTerminator(fileSystem, processControl, timing),
            fileSystem,
            clock,
            executableResolver,
            timing);
        return await new ServiceElection(seams, request, paths).RunAsync(cancellationToken).ConfigureAwait(false);
    }
}
