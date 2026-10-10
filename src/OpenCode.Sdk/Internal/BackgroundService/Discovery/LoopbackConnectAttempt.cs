using System.Net;
using System.Net.Sockets;

namespace OpenCode.Sdk.Internal.BackgroundService.Discovery;

/// <summary>One loopback connect the probe's transport is about to make; the transport owns the socket.</summary>
/// <param name="Socket">The socket to connect.</param>
/// <param name="EndPoint">The endpoint to connect it to.</param>
/// <param name="SynRetransmissionDisabled">Whether the transport provider accepted the no-SYN-retransmission option on this socket; false on a platform that does not take it.</param>
internal sealed record LoopbackConnectAttempt(Socket Socket, DnsEndPoint EndPoint, bool SynRetransmissionDisabled);
