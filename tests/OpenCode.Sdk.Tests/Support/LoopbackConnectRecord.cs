using System.Net.Sockets;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>One connect the probe's loopback transport made itself, as <see cref="RecordingLoopbackConnector"/> saw it.</summary>
/// <param name="SynRetransmissionDisabled">Whether the loopback option took on the socket it connected.</param>
/// <param name="TokenCanBeCanceled">Whether the token the transport passed to the connect could be cancelled.</param>
/// <param name="Failure">The connect's socket error, or null when it connected.</param>
internal sealed record LoopbackConnectRecord(bool SynRetransmissionDisabled, bool TokenCanBeCanceled, SocketError? Failure);
