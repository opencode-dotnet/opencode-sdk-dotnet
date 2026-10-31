namespace OpenCode.Sdk.Internal;

/// <summary>
/// The one API call the stop lifecycle makes before signalling: the pinned CLI's
/// <c>shutdownPersistentPty</c> (<c>server-connection.ts</c>), the generated
/// <c>experimental.persistentPty.shutdown</c> door against the registered endpoint. Behind a seam so the
/// orchestration runs without a socket; the shipped implementation rides the public client.
/// </summary>
internal interface IServicePtyShutdown
{
    /// <summary>Asks the registered daemon to stop its persistent-terminal daemon and every terminal it owns.</summary>
    /// <param name="registration">The registration whose endpoint and credential to use.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when the daemon answered.</returns>
    /// <exception cref="OpenCodeException">The daemon refused or could not be reached; the caller decides what that means.</exception>
    public Task ShutdownAsync(ServiceRegistration registration, CancellationToken cancellationToken);
}
