#if !NET
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Internal.BackgroundService.Abstractions;
using OpenCode.Sdk.Internal.BackgroundService.Discovery;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// A loopback connect that completes in a chosen order, with no socket traffic: it cancels the
/// given source first, then fails with the given exception, the order in which a connect completes
/// while its caller's token is being cancelled.
/// </summary>
/// <param name="cancelFirst">The source cancelled before the connect fails.</param>
/// <param name="failure">The connect's failure.</param>
internal sealed class ScriptedLoopbackConnector(CancellationTokenSource cancelFirst, Exception failure) : ILoopbackSocketConnector
{
    public async ValueTask ConnectAsync(LoopbackConnectAttempt attempt, CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(attempt);
        await cancelFirst.CancelOnWorkerAsync();
        throw failure;
    }
}
#endif
