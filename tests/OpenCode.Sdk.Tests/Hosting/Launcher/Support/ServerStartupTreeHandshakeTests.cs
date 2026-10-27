using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Support;
using Testably.Abstractions;

namespace OpenCode.Sdk.Tests.Hosting.Launcher.Support;

/// <summary>
/// The child-tree handshake's own bound, on a clock the test moves: when no tree connects back, the
/// failure names the bound, the step it was waiting for and the steps observed done before it, and the caller's cancellation stays a
/// cancellation. Nothing connects to the listener, so no process is started.
/// </summary>
public sealed class ServerStartupTreeHandshakeTests
{
    /// <summary>Bounds a handshake timer that is never armed; it decides nothing else.</summary>
    private static readonly TimeSpan HangGuard = TimeSpan.FromSeconds(30);

    [Test]
    [Timeout(60_000)]
    public async Task CaptureAndAcknowledgeAsync_Should_Name_Its_Bound_And_Step_When_No_Root_Connects(CancellationToken cancellationToken)
    {
        var time = new ManualTimeProvider();
        using var handshake = new ServerStartupTreeHandshake(new RealFileSystem(), time);
        handshake.Start();
        var capture = handshake.CaptureAndAcknowledgeAsync(acknowledge: true, cancellationToken);

        await time.WaitForArmedAsync(ServerStartupTreeHandshake.HandshakeTimeout, HangGuard);
        time.Advance(ServerStartupTreeHandshake.HandshakeTimeout);

        var failure = await Assert.That(capture).Throws<TimeoutException>();
        await Assert.That(failure!.Message)
            .IsEqualTo("The child-tree handshake did not complete within 15 s while waiting for the root to connect back. Steps observed done before the timeout: none; the timeout was observed 15.00 s after the handshake began.");
    }

    [Test]
    [Timeout(60_000)]
    public async Task CaptureAndAcknowledgeAsync_Should_Surface_The_Callers_Cancellation_As_Cancellation(CancellationToken cancellationToken)
    {
        var time = new ManualTimeProvider();
        using var caller = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
        using var handshake = new ServerStartupTreeHandshake(new RealFileSystem(), time);
        handshake.Start();
        var capture = handshake.CaptureAndAcknowledgeAsync(acknowledge: true, caller.Token);

        await time.WaitForArmedAsync(ServerStartupTreeHandshake.HandshakeTimeout, HangGuard);
        await caller.CancelOnWorkerAsync();

        _ = await Assert.That(capture).Throws<OperationCanceledException>();
    }
}
