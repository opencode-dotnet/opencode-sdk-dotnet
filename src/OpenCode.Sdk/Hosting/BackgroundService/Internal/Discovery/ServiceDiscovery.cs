namespace OpenCode.Sdk.Internal;

/// <summary>
/// The pinned client's <c>discover</c> and <c>incumbent</c>: locate and read the registration, then
/// probe its registered address for readiness or the caller's address for any compatible state.
/// The latter finds a compatible service that already owns that address. Both filter the reported
/// version when requested; refused input, caller cancellation and an unresolvable home propagate.
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
        var paths = await LocateAsync(selection, cancellationToken).ConfigureAwait(false);
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
        if (registration is null)
        {
            return null;
        }

        var answer = await probe.ProbeAsync(registration, cancellationToken).ConfigureAwait(false);
        if (!answer.IsReadyAndCompatible)
        {
            return null;
        }

        return ServiceVersionPolicy.MatchesVersion(answer.Version, expectedVersion) ? registration : null;
    }

    /// <summary>Finds the registered owner at the caller's address, in any compatible state.</summary>
    /// <param name="url">The address the service is expected to answer at.</param>
    /// <param name="options">The discovery options; null means every default.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The service and its state, or null when no compatible service answers.</returns>
    /// <exception cref="ArgumentException">The options are blank or contradictory, or a registration reads and the URL cannot serve as a base.</exception>
    /// <exception cref="OpenCodeServerException">No user home resolves for an XDG fallback.</exception>
    public async Task<ServiceIncumbent?> FindIncumbentAsync(Uri url, OpenCodeServerDiscoverOptions? options, CancellationToken cancellationToken)
    {
        var selection = ServiceSelection.Snapshot(options);
        var paths = await LocateAsync(selection, cancellationToken).ConfigureAwait(false);
        return await FindIncumbentAtAsync(paths, url, selection.ExpectedVersion, cancellationToken).ConfigureAwait(false);
    }

    /// <summary>Probes the registered owner at the caller's address using already-located paths.</summary>
    /// <param name="paths">The located paths.</param>
    /// <param name="url">The address the service is expected to answer at.</param>
    /// <param name="expectedVersion">The version the service must report, or null for any.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The service and its state, or null when no compatible service answers.</returns>
    /// <exception cref="ArgumentException">A registration reads and the URL cannot serve as a base.</exception>
    private async Task<ServiceIncumbent?> FindIncumbentAtAsync(ServicePaths paths, Uri url, string? expectedVersion, CancellationToken cancellationToken)
    {
        var registration = await ServiceRegistrationReader.TryReadAsync(fileSystem, paths.RegistrationFile, cancellationToken).ConfigureAwait(false);
        if (registration is null)
        {
            return null;
        }

        if (!ServiceRegistrationReader.CanBeBase(url))
        {
            throw new ArgumentException("url must be an absolute URL that can serve as a base URL.", nameof(url));
        }

        var copy = ServiceRegistrationReader.AtUrl(registration, url);
        if (copy is null)
        {
            return null;
        }

        var answer = await probe.ProbeAsync(copy, cancellationToken).ConfigureAwait(false);
        if (answer.State is not { } state || !answer.Compatible)
        {
            return null;
        }

        return ServiceVersionPolicy.MatchesVersion(answer.Version, expectedVersion) ? new ServiceIncumbent(copy, state) : null;
    }

    private async Task<ServicePaths> LocateAsync(ServiceSelection selection, CancellationToken cancellationToken)
    {
        cancellationToken.ThrowIfCancellationRequested();

        return await new ServiceRegistrationLocator(environment, fileSystem)
            .LocateAsync(selection, cancellationToken)
            .ConfigureAwait(false);
    }
}
