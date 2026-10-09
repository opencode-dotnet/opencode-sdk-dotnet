using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Ensure;
using OpenCode.Sdk.Internal.BackgroundService.Registration;
using OpenCode.Sdk.Internal.Diagnostics;

namespace OpenCode.Sdk.Internal.BackgroundService.Stop;

/// <summary>
/// The pinned CLI's <c>service stop</c> (<c>handlers/service/stop.ts</c> over <c>Service.stop</c>):
/// locate the registration the options name, read it and ask a ready and compatible daemon to shut
/// its persistent terminals down, as the CLI's own discovery does; then, as <c>Service.stop</c>
/// does, read the registration afresh, clear the handoff sidecar through the shared
/// <see cref="IServicePtyHandoff"/> seam, and hand the registration read last to the terminator. A
/// service that registered in between is the one stopped. A missing or corrupt registration is
/// nothing to stop; a shutdown the daemon refuses is ignored the way the CLI ignores it, and a
/// sidecar that cannot be cleared is ignored the way the client's <c>stop</c> ignores it, so the
/// terminate always runs; the caller's cancellation is the one thing that is not ignored.
/// </summary>
internal sealed class ServiceStopper(
    IServiceEnvironment environment,
    IServiceFileSystem fileSystem,
    IServiceInfoProbe probe,
    IServicePtyShutdown ptyShutdown,
    IServicePtyHandoff ptyHandoff,
    IServiceProcessControl processControl,
    ServiceTiming timing)
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
        var discovered = await ServiceRegistrationReader
            .TryReadAsync(fileSystem, paths.RegistrationFile, cancellationToken)
            .ConfigureAwait(false);
        if (discovered is not null)
        {
            await ShutdownPersistentTerminalsAsync(discovered, cancellationToken).ConfigureAwait(false);
        }

        // Service.stop reads the registration afresh: the exchange above can take seconds, and the
        // file can change hands meanwhile.
        var registration = await ServiceRegistrationReader
            .TryReadAsync(fileSystem, paths.RegistrationFile, cancellationToken)
            .ConfigureAwait(false);
        await ClearSidecarAsync(paths.RegistrationFile, cancellationToken).ConfigureAwait(false);
        if (registration is not null)
        {
            await new ServiceTerminator(fileSystem, processControl, timing)
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

    /// <summary>
    /// <c>ServerConnection.shutdownPersistentPty</c> (<c>server-connection.ts</c>): when a ready
    /// and compatible daemon answers, ask it to shut the terminals down. The caller's cancellation
    /// propagates; every other failure of the exchange is dropped, the CLI's <c>Effect.ignore</c>.
    /// </summary>
    [SlopwatchSuppress(
        "SW003",
        "The pinned CLI wraps this exchange in Effect.ignore (stop.ts): a refused, failed, or unreachable shutdown changes nothing, because the daemon is ended next either way.")]
    private async Task ShutdownPersistentTerminalsAsync(ServiceRegistration registration, CancellationToken cancellationToken)
    {
        var answer = await probe.ProbeAsync(registration, cancellationToken).ConfigureAwait(false);
        cancellationToken.ThrowIfCancellationRequested();
        if (!answer.IsReadyAndCompatible)
        {
            return;
        }

        try
        {
            await ptyShutdown.ShutdownAsync(registration, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenCodeException)
        {
            // Effect.ignore: the daemon is about to be ended either way.
        }
    }
}
