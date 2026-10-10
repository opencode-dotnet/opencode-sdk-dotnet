using OpenCode.Sdk.Internal.BackgroundService.Abstractions;

namespace OpenCode.Sdk.Internal.BackgroundService.Discovery;

/// <summary>The platform connect: the attempt's socket connects to its endpoint.</summary>
internal sealed class LoopbackSocketConnector : ILoopbackSocketConnector
{
    public ValueTask ConnectAsync(LoopbackConnectAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        return attempt.Socket.ConnectAsync(attempt.EndPoint, cancellationToken);
    }
}
