namespace OpenCode.Sdk.Internal;

/// <summary>
/// The pinned CLI's <c>service stop</c> (<c>handlers/service/stop.ts</c> over <c>Service.stop</c>):
/// locate the registration the options name, read it once, clear the handoff sidecar through the
/// shared <see cref="IServicePtyHandoff"/> seam, and hand the registration read to the terminator.
/// A missing or corrupt registration is nothing to stop; a sidecar that cannot be cleared is
/// ignored the way the client's <c>stop</c> ignores it, so the
/// terminate always runs; the caller's cancellation is the one thing that is not ignored.
/// </summary>
internal sealed class ServiceStopper(
    IServiceEnvironment environment,
    IServiceFileSystem fileSystem,
    IServicePtyHandoff ptyHandoff,
    IServiceProcessControl processControl,
    ServiceTiming timing,
    TimeProvider time)
{
    /// <summary>Stops the registered service a selection points at.</summary>
    /// <param name="options">The caller's options; null means every default.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when the registered process is gone or there was none to stop.</returns>
    /// <exception cref="ArgumentException">The options are blank or contradictory.</exception>
    /// <exception cref="OpenCodeServerException">No user home resolves for an XDG fallback, or the process survived the kill rung.</exception>
    public async Task StopAsync(OpenCodeServerStopOptions? options, CancellationToken cancellationToken)
    {
        var selection = ServiceSelection.Snapshot(options);
        cancellationToken.ThrowIfCancellationRequested();

        var paths = await new ServiceRegistrationLocator(environment, fileSystem)
            .LocateAsync(selection, cancellationToken)
            .ConfigureAwait(false);
        var registration = await ServiceRegistrationReader
            .TryReadAsync(fileSystem, paths.RegistrationFile, cancellationToken)
            .ConfigureAwait(false);
        await ClearSidecarAsync(paths.RegistrationFile, cancellationToken).ConfigureAwait(false);
        if (registration is not null)
        {
            await new ServiceTerminator(fileSystem, processControl, timing, time)
                .TerminateAsync(registration, paths.RegistrationFile, cancellationToken)
                .ConfigureAwait(false);
        }
    }

    /// <summary>
    /// <c>Service.stop</c>'s clear: best-effort, so a sidecar that cannot be removed never keeps
    /// the old service running. The caller's cancellation propagates.
    /// </summary>
    [SlopwatchSuppress(
        "SW003",
        "The pinned client's stop catches a failed clear and only warns (service.ts): terminal handoff is best-effort and must never keep the old service running.")]
    private async Task ClearSidecarAsync(string registrationFile, CancellationToken cancellationToken)
    {
        try
        {
            await ptyHandoff.ClearAsync(registrationFile, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenCodeServerException)
        {
            // Best-effort: the terminate runs next either way.
        }
    }
}
