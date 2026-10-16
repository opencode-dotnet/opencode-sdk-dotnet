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
        try
        {
            // The loop has passed its shutdown check when it calls accept; disposal lands right there.
            await LoopbackHttpServer.AcceptUntilShutdownAsync(async () =>
            {
                await shutdown.CancelOnWorkerAsync();
                listener.Stop();
                return await listener.AcceptTcpClientAsync();
            }, _ => served++, shutdown.Token).WaitAsync(HangGuard.Bound, CancellationToken.None);
        }
        finally
        {
            listener.Stop();
        }

        await Assert.That(served).IsEqualTo(0);
    }

    [Test]
    public async Task ServeExchangeAsync_Should_End_As_Teardown_When_A_Released_Connection_Is_No_Longer_Readable()
    {
        await using var server = LoopbackHttpServer.Start(_ => throw new InvalidOperationException("No request is served."));
        server.ReleaseResponses();
        var released = new MemoryStream();
        await released.DisposeAsync();

        // The release closed the socket after GetStream and before the request reader took it.
        await server.ServeExchangeAsync(() => released).WaitAsync(HangGuard.Bound, CancellationToken.None);

        await Assert.That(server.ClientDisconnected.IsCompleted).IsTrue();
    }

    [Test]
    public async Task ServeExchangeAsync_Should_Fault_When_An_Unreleased_Connection_Is_Not_Readable()
    {
        await using var server = LoopbackHttpServer.Start(_ => throw new InvalidOperationException("No request is served."));
        var unreadable = new MemoryStream();
        await unreadable.DisposeAsync();

        _ = await Assert.That(server.ServeExchangeAsync(() => unreadable).WaitAsync(HangGuard.Bound, CancellationToken.None))
            .Throws<ArgumentException>();
    }
}
