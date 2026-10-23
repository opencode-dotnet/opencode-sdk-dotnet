using System.Net;
using System.Net.Sockets;

namespace OpenCode.Sdk.Internal;

/// <summary>The owned loopback connect on a socket prepared to refuse closed ports without SYN retransmission.</summary>
internal sealed class LoopbackConnect
#if NET
    (ILoopbackSocketConnector connector)
#endif
{
    /// <summary><c>_WSAIOW(IOC_VENDOR, 17)</c>, the Winsock control code for <c>TCP_INITIAL_RTO_PARAMETERS</c>.</summary>
    private const int SioTcpInitialRto = unchecked((int)0x98000011);

    /// <summary>
    /// <c>TCP_INITIAL_RTO_PARAMETERS { Rtt = TCP_INITIAL_RTO_UNSPECIFIED_RTT (0xFFFF),
    /// MaxSynRetransmissions = TCP_INITIAL_RTO_NO_SYN_RETRANSMISSIONS (0xFE) }</c>, little-endian
    /// with its trailing padding byte; Windows 10 1709 and later honour the no-retransmission value.
    /// </summary>
    private static readonly byte[] NoSynRetransmissions = [0xFF, 0xFF, 0xFE, 0x00];

#if NET
    /// <summary>The default connect of <see cref="SocketsHttpHandler"/>, on a socket that took the loopback option first.</summary>
    public async ValueTask<Stream> ConnectWithoutSynRetransmissionAsync(SocketsHttpConnectionContext context, CancellationToken cancellationToken)
    {
        var host = context.DnsEndPoint.Host;
        var isLoopback = string.Equals(host, "localhost", StringComparison.OrdinalIgnoreCase)
            || (IPAddress.TryParse(host, out var address) && IsLoopback(address));
        var attempt = Prepare(context.DnsEndPoint, isLoopback);
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

    /// <summary>
    /// Whether an address is loopback as <see cref="Uri.IsLoopback"/> counts it. A scope id names an
    /// interface, and <c>::1</c> is loopback on every one, but <see cref="IPAddress.IsLoopback"/>
    /// compares the scope too, so the comparison drops it.
    /// </summary>
    private static bool IsLoopback(IPAddress address) =>
        IPAddress.IsLoopback(address.AddressFamily == AddressFamily.InterNetworkV6 && address.ScopeId != 0
            ? new IPAddress(address.GetAddressBytes())
            : address);
#endif

    /// <summary>The one socket setup both loopback connects share: a new socket that took the loopback option where the platform offers it.</summary>
    internal static LoopbackConnectAttempt Prepare(DnsEndPoint endPoint) => Prepare(endPoint, disableSynRetransmission: true);

    private static LoopbackConnectAttempt Prepare(DnsEndPoint endPoint, bool disableSynRetransmission)
    {
        Socket? socket = null;
        try
        {
            socket = new Socket(SocketType.Stream, ProtocolType.Tcp) { NoDelay = true };
            var attempt = new LoopbackConnectAttempt(socket, endPoint, disableSynRetransmission && DisableSynRetransmission(socket));
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
