using System.Collections.Concurrent;
using System.Net.Sockets;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Discovery;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The platform connect under the probe's loopback transport, recording each connect the transport
/// made itself: whether the loopback option took on the socket it connected, whether the token it
/// passed could be cancelled, and how the connect failed.
/// </summary>
internal sealed class RecordingLoopbackConnector : ILoopbackSocketConnector
{
    private readonly LoopbackSocketConnector _platform = new();
    private readonly ConcurrentQueue<LoopbackConnectRecord> _records = new();

    /// <summary>Gets every connect so far, in order.</summary>
    public IReadOnlyList<LoopbackConnectRecord> Records => [.. _records];

    public async ValueTask ConnectAsync(LoopbackConnectAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        try
        {
            await _platform.ConnectAsync(attempt, cancellationToken);
            _records.Enqueue(new LoopbackConnectRecord(attempt.SynRetransmissionDisabled, cancellationToken.CanBeCanceled, Failure: null));
        }
        catch (SocketException failure)
        {
            _records.Enqueue(new LoopbackConnectRecord(attempt.SynRetransmissionDisabled, cancellationToken.CanBeCanceled, failure.SocketErrorCode));
            throw;
        }
    }
}
