using System.Net.Sockets;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.Diagnostics;

namespace OpenCode.Sdk.Internal.BackgroundService.Discovery;

/// <summary>
/// The discovery probe's own transport: the non-redirecting handler for one info exchange against
/// the registered endpoint, with two loopback rules the pipeline's transport does not
/// carry. A loopback endpoint is never routed through a proxy, because an environment proxy
/// without <c>NO_PROXY</c> would hide a live daemon. On Windows a loopback connect disables SYN
/// retransmission (<c>SIO_TCP_INITIAL_RTO</c>), because the default connect reports a refused
/// port only after about two seconds — past the pinned probe bound — where Linux, macOS, and the
/// pinned client's own runtime refuse at once; libuv, Go, Bun, curl, and Chromium set the same
/// option for loopback. A closed loopback port is therefore refused at once on every host, and the
/// probe classifies the refusal as no service. Separate from <see cref="TransportPolicy"/> so
/// neither rule reaches a public client.
/// </summary>
internal sealed class LoopbackTransport(ILoopbackSocketConnector connector) : IServiceProbeTransport
{
    /// <summary><c>_WSAIOW(IOC_VENDOR, 17)</c>, the Winsock control code for <c>TCP_INITIAL_RTO_PARAMETERS</c>.</summary>
    private const int SioTcpInitialRto = unchecked((int)0x98000011);

    /// <summary>
    /// <c>TCP_INITIAL_RTO_PARAMETERS { Rtt = TCP_INITIAL_RTO_UNSPECIFIED_RTT (0xFFFF),
    /// MaxSynRetransmissions = TCP_INITIAL_RTO_NO_SYN_RETRANSMISSIONS (0xFE) }</c>, little-endian
    /// with its trailing padding byte; Windows 10 1709 and later honour the no-retransmission value.
    /// </summary>
    private static readonly byte[] NoSynRetransmissions = [0xFF, 0xFF, 0xFE, 0x00];

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
            handler.ConnectCallback = ConnectWithoutSynRetransmissionAsync;
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

#if NET
    /// <summary>The default connect of <see cref="SocketsHttpHandler"/>, on a socket that took the loopback option first.</summary>
    private async ValueTask<Stream> ConnectWithoutSynRetransmissionAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var attempt = Prepare(context.DnsEndPoint);
        var socket = attempt.Socket;
        try
        {
            await connector.ConnectAsync(attempt, cancellationToken).ConfigureAwait(false);
            var stream = new NetworkStream(socket, ownsSocket: true);
            socket = null;
            return stream;
        }
        finally
        {
            socket?.Dispose();
        }
    }
#else
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

        var attempt = Prepare(new System.Net.DnsEndPoint(endpoint.IdnHost, endpoint.Port));
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

    /// <summary>The one socket setup both loopback connects share: a new socket that took the loopback option where the platform offers it.</summary>
    private static LoopbackConnectAttempt Prepare(System.Net.DnsEndPoint endPoint)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var attempt = new LoopbackConnectAttempt(socket, endPoint, DisableSynRetransmission(socket));
            socket = null;
            return attempt;
        }
        finally
        {
            socket?.Dispose();
        }
    }

    /// <summary>
    /// Applies the no-retransmission parameters on Windows; where the transport provider refuses
    /// them (Windows before 10 1709) the default connect and its delay remain.
    /// The vendor control code throws on Unix, so the platform check is the guard, not the catch.
    /// </summary>
    /// <returns>True when the transport provider accepted the parameters; false on another platform or when it refused them.</returns>
    [SlopwatchSuppress(
        "SW003",
        "The option is best effort wherever it is set (libuv, Go, the pinned client's runtime): a Windows build that refuses it keeps the default retransmissions, and the connect still runs under the probe's bound.")]
    internal static bool DisableSynRetransmission(Socket socket)
    {
        if (!OperatingSystem.IsWindows())
        {
            return false;
        }

        try
        {
            _ = socket.IOControl(SioTcpInitialRto, NoSynRetransmissions, optionOutValue: null);
            return true;
        }
        catch (SocketException)
        {
            // Best effort: the default retransmissions remain, bounded by the probe's timeout.
            return false;
        }
    }
}
