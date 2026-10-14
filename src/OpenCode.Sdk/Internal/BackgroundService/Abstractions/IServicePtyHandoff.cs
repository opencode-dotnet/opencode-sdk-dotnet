namespace OpenCode.Sdk.Internal;

/// <summary>
/// The persistent-terminal handoff sidecar the pinned client's <c>PtyHandoff</c> keeps beside the
/// registration. Methods match upstream: <c>prepare</c>, <c>environment</c>, <c>complete</c>,
/// <c>clear</c>. The shipped implementation is <see cref="ServicePtyHandoff"/>.
/// </summary>
internal interface IServicePtyHandoff
{
    /// <summary>
    /// Publishes a handoff ticket before the current owner is stopped, so a replacement contender
    /// can adopt the persistent terminals.
    /// </summary>
    /// <param name="registrationFile">The registration path the sidecar sits beside.</param>
    /// <param name="registration">The owner being replaced.</param>
    /// <param name="timeout">The bound on the handoff HTTP exchange.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when the sidecar is published, or when there is nothing to publish.</returns>
    public Task PrepareAsync(
        string registrationFile,
        ServiceRegistration registration,
        TimeSpan timeout,
        CancellationToken cancellationToken);

    /// <summary>
    /// Returns the caller environment with the handoff ticket applied last as
    /// <c>OPENCODE_PTY_HANDOFF</c>, or with that variable removed when there is no live ticket.
    /// </summary>
    /// <param name="registrationFile">The registration path the sidecar sits beside.</param>
    /// <param name="callerEnvironment">The config-plus-caller overlay; null means none.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>The overlay the contender spawn layers over the process environment.</returns>
    public Task<IReadOnlyDictionary<string, string?>> EnvironmentAsync(
        string registrationFile,
        IReadOnlyDictionary<string, string>? callerEnvironment,
        CancellationToken cancellationToken);

    /// <summary>
    /// Drops the sidecar when the winning service is not the one the ticket was prepared for.
    /// </summary>
    /// <param name="registrationFile">The registration path the sidecar sits beside.</param>
    /// <param name="registration">The service that won the election.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when the sidecar has been considered.</returns>
    public Task CompleteAsync(
        string registrationFile,
        ServiceRegistration registration,
        CancellationToken cancellationToken);

    /// <summary>Removes the sidecar, the way upstream's <c>PtyHandoff.clear</c> does.</summary>
    /// <param name="registrationFile">The registration path the sidecar sits beside.</param>
    /// <param name="cancellationToken">The caller's token.</param>
    /// <returns>A task that completes when the sidecar is gone or was never there.</returns>
    public Task ClearAsync(string registrationFile, CancellationToken cancellationToken);
}
