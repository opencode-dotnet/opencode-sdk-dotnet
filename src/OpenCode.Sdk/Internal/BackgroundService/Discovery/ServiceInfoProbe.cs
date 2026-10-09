using System.Net;
using System.Net.Http.Headers;
using System.Text;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Ensure;
using OpenCode.Sdk.Internal.BackgroundService.Registration;

namespace OpenCode.Sdk.Internal.BackgroundService.Discovery;

/// <summary>
/// Performs the pinned client's authenticated info exchange over the probe's own loopback-aware
/// transport (<see cref="LoopbackTransport"/>). The probe decodes only pid/version; the public
/// generated info model has a different contract. Discovery keeps its own request bound and never
/// forwards credentials on redirects.
/// </summary>
internal sealed class ServiceInfoProbe(ServiceTiming timing) : IServiceInfoProbe
{
    /// <summary>The pin's info route, resolved against the authority only, as <c>new URL("/api/info", info.url)</c> does.</summary>
    private const string InfoPath = "/api/info";

    private static readonly ServiceProbeResult NoService = new(State: null, Version: null, TimedOut: false, Compatible: true);
    private static readonly ServiceProbeResult Expired = new(State: null, Version: null, TimedOut: true, Compatible: true);

    public async Task<ServiceProbeResult> ProbeAsync(ServiceRegistration registration, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(registration);
        cancellationToken.ThrowIfCancellationRequested();

        using var client = LoopbackTransport.CreateProbeClient(registration.Endpoint);
        using var request = new HttpRequestMessage(HttpMethod.Get, new Uri(registration.Endpoint, InfoPath));
        if (registration.Password is { } password)
        {
            // UTF-8, as upstream's probe and the pipeline encode the same credential and the
            // daemon decodes it.
            request.Headers.Authorization = new AuthenticationHeaderValue(
                "Basic", Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + password)));
        }

        // The internal bound and the caller's token are separate: only the caller's propagates.
        using var bound = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        bound.CancelAfter(timing.RequestTimeout);

        HttpStatusCode status;
        byte[] body;
        try
        {
#if !NET
            // HttpClientHandler has no connect seam, so on Windows a refused loopback port would
            // surface only after the SYN retransmissions, past the bound: the refusal is learned
            // first over the transport's raw pre-connect (LoopbackTransport), inside the same bound.
            if (registration.Endpoint.IsLoopback
                && OperatingSystem.IsWindows()
                && !await LoopbackTransport.IsListeningAsync(registration.Endpoint, bound.Token).ConfigureAwait(false))
            {
                return NoService;
            }
#endif
            using var response = await client
                .SendAsync(request, HttpCompletionOption.ResponseHeadersRead, bound.Token)
                .ConfigureAwait(false);
            status = response.StatusCode;

            // An authenticated 404 is classified without its body, as the pinned client does.
            body = status == HttpStatusCode.NotFound
                ? []
                : await response.Content.ReadAsByteArrayAsync(bound.Token).ConfigureAwait(false);
        }
        catch (Exception failure) when (IsTransportFailure(failure))
        {
            return ClassifyFailure(cancellationToken, bound.Token);
        }

        return ServiceProbeResponseClassifier.Classify(registration, status, body);
    }

    /// <summary>The ways an exchange fails rather than answers: the bound's or the caller's cancellation, a refused or dropped connection, a body read cut short.</summary>
    /// <param name="exception">The failure.</param>
    /// <returns>True when the exchange itself failed.</returns>
    internal static bool IsTransportFailure(Exception exception) =>
        exception is OperationCanceledException or HttpRequestException or IOException or ObjectDisposedException;

    /// <summary>
    /// The pinned client's <c>probeResult</c> reads every rejected exchange the same way:
    /// <c>timedOut: signal.aborted</c>. Whether the bound expired decides, never the failure's type:
    /// .NET Framework's handler can report the bound's abort as an <see cref="HttpRequestException"/>,
    /// which must still count toward the three-timeout recovery. The caller's cancellation propagates.
    /// </summary>
    /// <param name="caller">The caller's token.</param>
    /// <param name="bound">The probe's internal request bound.</param>
    /// <returns>A timeout when the bound expired; otherwise no service.</returns>
    internal static ServiceProbeResult ClassifyFailure(CancellationToken caller, CancellationToken bound)
    {
        caller.ThrowIfCancellationRequested();
        return bound.IsCancellationRequested ? Expired : NoService;
    }
}
