namespace OpenCode.Sdk.Internal;

/// <summary>
/// What one info probe learned, in the pinned client's own return shape (<c>probeResult</c>):
/// the service, its state, and its compatibility when the answer was this daemon's, whether the
/// internal bound expired, and nothing else. It carries no credential, so its rendering is safe.
/// </summary>
/// <param name="State">The daemon's state, or null when the answer was not a usable service.</param>
/// <param name="Version">The version the info answer reported, when there was one.</param>
/// <param name="TimedOut">Whether the internal request bound expired before an answer arrived.</param>
/// <param name="Compatible">
/// Whether the daemon speaks this protocol: the pinned client reports an authenticated 404 on the
/// info route as the registered daemon, present and ready, but incompatible.
/// </param>
internal sealed record ServiceProbeResult(ServiceState? State, string? Version, bool TimedOut, bool Compatible)
{
    /// <summary>Gets a value indicating whether the answer identified this registration's daemon.</summary>
    public bool IsService => State is not null;

    /// <summary>Gets a value indicating whether the daemon answered ready in this protocol: the one state Discover returns and Stop asks to shut its terminals down.</summary>
    public bool IsReadyAndCompatible => State == ServiceState.Ready && Compatible;
}
