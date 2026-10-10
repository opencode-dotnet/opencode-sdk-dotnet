using OpenCode.Sdk.Internal.BackgroundService.Discovery;

namespace OpenCode.Sdk.Internal.BackgroundService.Abstractions;

/// <summary>
/// The socket connect under the probe's loopback transport. Behind a seam so a test sees each
/// socket the transport connects, with whether it took the loopback option, and decides when a
/// connect completes and how.
/// </summary>
internal interface ILoopbackSocketConnector
{
    /// <summary>Connects the attempt's socket to its endpoint.</summary>
    /// <param name="attempt">The prepared socket, its endpoint, and whether the loopback option took.</param>
    /// <param name="cancellationToken">The token that cancels the connect.</param>
    /// <returns>A task that completes when the socket is connected.</returns>
    public ValueTask ConnectAsync(LoopbackConnectAttempt attempt, CancellationToken cancellationToken);
}
