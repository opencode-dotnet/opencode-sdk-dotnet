namespace OpenCode.Sdk.Internal.BackgroundService.Abstractions;

/// <summary>
/// The transport the info probe exchanges over: the handler for one request against a registered
/// endpoint and, where that handler has no connect seam, the pre-connect that learns a refusal
/// first. Behind a seam so a test can stand between the probe and its transport and decide when
/// the exchange's outcome reaches the probe.
/// </summary>
internal interface IServiceProbeTransport
{
    /// <summary>Creates the handler one info exchange runs over; the caller owns it.</summary>
    /// <param name="endpoint">The registered endpoint.</param>
    /// <returns>The handler.</returns>
    public HttpMessageHandler CreateHandler(Uri endpoint);

#if !NET
    /// <summary>Learns whether the endpoint accepts a connection, before the exchange.</summary>
    /// <param name="endpoint">The registered endpoint.</param>
    /// <param name="cancellationToken">The probe's bound, linked to the caller's token.</param>
    /// <returns>True when the endpoint accepted a connection; false when it refused one or the connect failed otherwise.</returns>
    public Task<bool> IsListeningAsync(Uri endpoint, CancellationToken cancellationToken);
#endif
}
