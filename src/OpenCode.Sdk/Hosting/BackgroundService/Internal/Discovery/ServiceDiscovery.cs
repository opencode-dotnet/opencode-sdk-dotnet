namespace OpenCode.Sdk.Internal;

/// <summary>
/// The pinned client's <c>discover</c>: locate the registration (paths plus the channel's legacy
/// migration), read it, probe the daemon, and hand back its identity only when it is ready and, if
/// the caller asked, at the expected version. Everything unusable is "no service"; only refused
/// input, the caller's cancellation, and an unresolvable home are failures.
/// </summary>
internal sealed class ServiceDiscovery(
    IServiceEnvironment environment,
    IServiceFileSystem fileSystem,
    IServiceInfoProbe probe)
{
    /// <summary>Discovers the registered daemon a selection points at.</summary>
    /// <param name="options">The caller's options; null means every default.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The ready daemon's registration, or null when there is no usable service.</returns>
    /// <exception cref="ArgumentException">The options are blank or contradictory.</exception>
    /// <exception cref="OpenCodeServerException">No user home resolves for an XDG fallback.</exception>
    public async Task<ServiceRegistration?> DiscoverAsync(OpenCodeServerDiscoverOptions? options, CancellationToken cancellationToken)
    {
        var selection = ServiceSelection.Snapshot(options);
        cancellationToken.ThrowIfCancellationRequested();

        var paths = await new ServiceRegistrationLocator(environment, fileSystem)
            .LocateAsync(selection, cancellationToken)
            .ConfigureAwait(false);
        return await DiscoverAtAsync(paths, selection.ExpectedVersion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Discovers the daemon registered at already-located paths.</summary>
    /// <param name="paths">The located paths.</param>
    /// <param name="expectedVersion">The version the daemon must report, or null for any.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The ready daemon's registration, or null when there is no usable service.</returns>
    public async Task<ServiceRegistration?> DiscoverAtAsync(ServicePaths paths, string? expectedVersion, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(paths);

        var registration = await ServiceRegistrationReader
            .TryReadAsync(fileSystem, paths.RegistrationFile, cancellationToken)
            .ConfigureAwait(false);
        if (registration is null || registration.Password is null)
        {
            // A registration without a password cannot materialize the handle's non-null
            // credential contract; the pinned daemon always publishes one.
            return null;
        }

        var answer = await probe.ProbeAsync(registration, cancellationToken).ConfigureAwait(false);
        if (!answer.IsReadyAndCompatible)
        {
            return null;
        }

        return ServiceVersionPolicy.MatchesVersion(answer.Version, expectedVersion) ? registration : null;
    }
}
