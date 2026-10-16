using System.Net;
using System.Net.Sockets;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

public sealed class LoopbackHttpServerTests
{
    [Test]
    public async Task AcceptUntilShutdownAsync_Should_End_When_Shutdown_Stops_The_Listener_Between_The_Check_And_The_Accept()
    {
#if NET
        using var listener = new TcpListener(IPAddress.Loopback, 0);
#else
        var listener = new TcpListener(IPAddress.Loopback, 0);
#endif
        listener.Start();
        using var shutdown = new CancellationTokenSource();
        var served = 0;

        // The loop has passed its shutdown check when it calls accept; disposal lands right there.
        await LoopbackHttpServer.AcceptUntilShutdownAsync(async () =>
        {
            await shutdown.CancelOnWorkerAsync();
            listener.Stop();
            return await listener.AcceptTcpClientAsync();
        }, _ => served++, shutdown.Token).WaitAsync(HangGuard.Bound, CancellationToken.None);

        await Assert.That(served).IsEqualTo(0);
    }
}
