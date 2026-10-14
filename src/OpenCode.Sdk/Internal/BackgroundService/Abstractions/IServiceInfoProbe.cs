namespace OpenCode.Sdk.Internal;

/// <summary>
/// The one network exchange discovery performs: an authenticated info request against a
/// registration's endpoint, classified into the daemon's state. Behind a seam so the orchestration
/// is tested without a socket and so the pin's route and identity decoder live in one place.
/// </summary>
internal interface IServiceInfoProbe
{
    /// <summary>Asks the registered daemon for its info: identity, urls, paths, and readiness.</summary>
    /// <param name="registration">The registration whose endpoint and credential to use.</param>
    /// <param name="cancellationToken">The caller's token; its cancellation propagates, the internal bound does not.</param>
    /// <returns>The classified answer.</returns>
    public Task<ServiceProbeResult> ProbeAsync(ServiceRegistration registration, CancellationToken cancellationToken);
}
