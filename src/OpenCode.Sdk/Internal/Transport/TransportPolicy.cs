using System.Diagnostics;
using System.Globalization;
#if !NET
using System.Net;
#endif

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The terminal policy: sends the request over the owned or injected client, classifies send
/// failures, and refuses undeclared redirects — a redirect is a protocol invariant no
/// operation can declare, so 3xx is transport's rule rather than any operation table's.
/// Owns the client's lifetime when the pipeline owns the client.
/// Knowledge source: BCL-derived — <see cref="HttpClient"/> send behavior per target runtime.
/// </summary>
internal sealed class TransportPolicy : PipelinePolicy, IDisposable
{
    internal const int ConnectionLifetimeMilliseconds = 120_000;

    /// <summary>
    /// How long an idle pooled connection may wait for reuse: the pinned server's
    /// <c>Keep-Alive: timeout=5</c> hint minus the 2-second threshold upstream's client takes off it
    /// (undici's <c>keepAliveTimeoutThreshold</c>), so a socket the server is about to close is
    /// never picked up for a request. Modern handlers already stop at the hint minus one second;
    /// .NET Framework ignores the hint, so there this is the only limit below the server's close.
    /// </summary>
    internal const int ConnectionIdleMilliseconds = 3_000;

    private readonly HttpClient _httpClient;
    private readonly bool _ownsHttpClient;

    public TransportPolicy(HttpClient httpClient, bool ownsHttpClient)
    {
        ArgumentNullException.ThrowIfNull(httpClient);

        _httpClient = httpClient;
        _ownsHttpClient = ownsHttpClient;
    }

#if NET
    [System.Runtime.CompilerServices.AsyncMethodBuilder(typeof(System.Runtime.CompilerServices.PoolingAsyncValueTaskMethodBuilder))]
#endif
    public override async ValueTask ProcessAsync(PipelineMessage message, ReadOnlyMemory<PipelinePolicy> remaining)
    {
        Debug.Assert(remaining.IsEmpty, "The transport is the pipeline's terminal policy.");
#if !NET
        if (_ownsHttpClient)
        {
            ConfigureDownlevelServicePoint(message.Request.RequestUri!);
        }
#endif
        try
        {
            // The send runs under the network token — the caller's token with the progress
            // window and client lifetime linked over it — while classification keeps their
            // identities apart, so only a window expiry reports as the transport timing out.
            message.Response = await _httpClient
                .SendAsync(message.Request, HttpCompletionOption.ResponseHeadersRead, message.NetworkToken)
                .ConfigureAwait(false);
        }
        catch (Exception exception) when (FailureClassification.Handles(exception, FailurePhase.Send))
        {
            throw FailureClassification.Map(exception, FailurePhase.Send, message.CancellationToken, message.LifetimeToken);
        }

        RefuseRedirectStatus((int)message.Response.StatusCode);
    }

    public void Dispose()
    {
        if (_ownsHttpClient)
        {
            _httpClient.Dispose();
        }
    }

    /// <summary>Builds the non-redirecting owned transport with endpoint rotation on every runtime.</summary>
    public static HttpClient CreateOwnedHttpClient(Uri endpoint)
    {
        HttpMessageHandler? handler = null;
        try
        {
            handler = CreateOwnedHttpHandler(endpoint);
            var httpClient = new HttpClient(handler, disposeHandler: true)
            {
                // The pipeline owns timeouts through the progress window; two mechanisms
                // must not race on the owned transport.
                Timeout = Timeout.InfiniteTimeSpan,
            };
            handler = null;
            return httpClient;
        }
        finally
        {
            handler?.Dispose();
        }
    }

    /// <summary>Creates the real platform handler; internal so tests can observe its sealed policy.</summary>
    internal static HttpMessageHandler CreateOwnedHttpHandler(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
#if NET
        return CreateOwnedHttpHandler(endpoint, new LoopbackSocketConnector());
#else
        ConfigureDownlevelServicePoint(endpoint);
        return new HttpClientHandler
        {
            AllowAutoRedirect = false,
        };
#endif
    }

#if NET
    internal static HttpMessageHandler CreateOwnedHttpHandler(Uri endpoint, ILoopbackSocketConnector connector)
    {
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            PooledConnectionLifetime = TimeSpan.FromMilliseconds(ConnectionLifetimeMilliseconds),
            PooledConnectionIdleTimeout = TimeSpan.FromMilliseconds(ConnectionIdleMilliseconds),
        };
        if (endpoint.IsLoopback && OperatingSystem.IsWindows())
        {
            handler.ConnectCallback = new LoopbackConnect(connector).ConnectWithoutSynRetransmissionAsync;
        }

        return handler;
    }
#endif

#if !NET
    private static void ConfigureDownlevelServicePoint(Uri endpoint)
    {
        var servicePoint = ServicePointManager.FindServicePoint(endpoint, WebRequest.DefaultWebProxy);
        servicePoint.ConnectionLimit = int.MaxValue;
        servicePoint.ConnectionLeaseTimeout = ConnectionLifetimeMilliseconds;
        servicePoint.MaxIdleTime = ConnectionIdleMilliseconds;
    }
#endif

    private static void RefuseRedirectStatus(int status)
    {
        if (status is >= 300 and < 400)
        {
            throw new OpenCodeTransportException($"The opencode API returned undeclared redirect status {status.ToString(CultureInfo.InvariantCulture)}.");
        }
    }
}
