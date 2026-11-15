namespace OpenCode.Sdk.Internal;

/// <summary>The stop's shared best-effort handoff or clear, before the service is terminated.</summary>
internal sealed class ServicePtyHandoffStep(IServicePtyHandoff handoff, TimeSpan requestTimeout)
{
    /// <summary>
    /// <c>stop</c>'s handoff step: a failure to prepare or clear is only warned about upstream, so
    /// the terminate that follows always runs. The caller's cancellation still propagates.
    /// </summary>
    [SlopwatchSuppress(
        "SW003",
        "The pinned client's stop catches every handoff or clear failure and only warns (stop in promise/service.ts and effect/service.ts); the SDK has no warning channel here, so the failure is dropped. Only the caller's cancellation propagates, so the old service is still terminated.")]
    public async Task RunAsync(string registrationFile, ServiceRegistration? registration, bool handOff, CancellationToken cancellationToken)
    {
        try
        {
            if (handOff && registration is not null)
            {
                await handoff
                    .PrepareAsync(registrationFile, registration, requestTimeout, cancellationToken)
                    .ConfigureAwait(false);
            }
            else
            {
                await handoff.ClearAsync(registrationFile, cancellationToken).ConfigureAwait(false);
            }
        }
        catch (Exception failure) when (failure is not OperationCanceledException || !cancellationToken.IsCancellationRequested)
        {
            // Best-effort: the old service is terminated next either way.
        }
    }
}
