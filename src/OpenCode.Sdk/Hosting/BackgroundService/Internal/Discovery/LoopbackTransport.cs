#if !NET
using System.Net.Sockets;
#endif

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The discovery probe's own transport: the non-redirecting handler for one info exchange against
/// the registered endpoint. A loopback endpoint is never routed through a proxy, because an
/// environment proxy without <c>NO_PROXY</c> would hide a live daemon. The connect uses
/// <see cref="LoopbackConnect"/> so a refused loopback port reaches the probe before its bound.
/// </summary>
internal sealed class LoopbackTransport(ILoopbackSocketConnector connector) : IServiceProbeTransport
{
    /// <summary>Creates the probe's handler: no proxy for loopback, no redirect, and on Windows the loopback connect below.</summary>
    public HttpMessageHandler CreateHandler(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);
#if NET
        var handler = new SocketsHttpHandler
        {
            AllowAutoRedirect = false,
            UseProxy = !endpoint.IsLoopback,
        };
        if (endpoint.IsLoopback && OperatingSystem.IsWindows())
        {
            handler.ConnectCallback = new LoopbackConnect(connector).ConnectWithoutSynRetransmissionAsync;
        }

        return handler;
#else
        return new HttpClientHandler
        {
            AllowAutoRedirect = false,
            UseProxy = !endpoint.IsLoopback,
        };
#endif
    }

#if !NET
    /// <summary>
    /// The downlevel answer to a refused loopback port: <see cref="HttpClientHandler"/> has no connect
    /// seam, so the probe learns a refusal first over a raw socket that disables SYN retransmission,
    /// and sends the request only when the port listens. A live daemon costs one extra loopback
    /// handshake; a dead one is reported at once. The connect itself takes no token, because the
    /// downlevel token-taking connect reports a cancellation in place of a refusal that completed
    /// while the token was being cancelled: the token instead closes the socket, and
    /// <see cref="ClassifyConnectFailure"/> reads the connect's own result.
    /// </summary>
    /// <returns>True when the endpoint accepted a connection; false when it refused one or the connect failed otherwise.</returns>
    /// <exception cref="OperationCanceledException">The token was cancelled before the endpoint answered.</exception>
    public async Task<bool> IsListeningAsync(Uri endpoint, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        var attempt = LoopbackConnect.Prepare(new System.Net.DnsEndPoint(endpoint.IdnHost, endpoint.Port));
        using var socket = attempt.Socket;
        using var abort = cancellationToken.Register(static state => ((Socket)state).Dispose(), socket);
        try
        {
            await connector.ConnectAsync(attempt, CancellationToken.None).ConfigureAwait(false);
            return true;
        }
        catch (SocketException failure)
        {
            return ClassifyConnectFailure(failure, cancellationToken);
        }
        catch (ObjectDisposedException) when (cancellationToken.IsCancellationRequested)
        {
            throw new OperationCanceledException(cancellationToken);
        }
    }

    /// <summary>
    /// Reads a failed pre-connect. A refusal is the endpoint's own answer, so it stands even when
    /// the token was cancelled meanwhile. Any other failure while the token is cancelled counts as
    /// the token's cancellation, as the token-taking connect reported it: the closed socket's
    /// aborted connect is the usual one, but not the only one. Any other failure while the token
    /// stands is a port that does not listen.
    /// </summary>
    /// <param name="failure">The connect's failure.</param>
    /// <param name="cancellationToken">The token that closes the socket when cancelled.</param>
    /// <returns>False: the endpoint does not listen.</returns>
    /// <exception cref="OperationCanceledException">The failure is not a refusal and the token was cancelled.</exception>
    internal static bool ClassifyConnectFailure(SocketException failure, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(failure);
        if (failure.SocketErrorCode != SocketError.ConnectionRefused)
        {
            cancellationToken.ThrowIfCancellationRequested();
        }

        return false;
    }
#endif
}
