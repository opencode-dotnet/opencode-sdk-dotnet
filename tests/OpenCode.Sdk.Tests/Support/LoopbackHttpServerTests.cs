using System.Net;
using System.Net.Sockets;
using System.Text;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk.Tests.Support;

public sealed class LoopbackHttpServerTests
{
    [Test]
    public async Task ServeExchangeAsync_Should_Close_A_KeepOpen_Request_That_Arrives_After_The_Release()
    {
        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpResponse
        {
            StatusCode = HttpStatusCode.ServiceUnavailable,
            KeepOpen = true,
        });
        server.ReleaseResponses();
        using var client = new TcpClient();

        var read = await ReadResponseStartAsync(client, server.Endpoint);

        await Assert.That(server.ReleasedHoldClosed).IsTrue();
        await Assert.That(read).IsEqualTo(0);
    }

    [Test]
    public async Task ServeExchangeAsync_Should_Serve_A_Non_KeepOpen_Request_That_Arrives_After_The_Release()
    {
        await using var server = LoopbackHttpServer.Start(_ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.OK });
        server.ReleaseResponses();
        using var client = new TcpClient();

        var read = await ReadResponseStartAsync(client, server.Endpoint);

        await Assert.That(read).IsGreaterThan(0);
        await Assert.That(server.ReleasedHoldClosed).IsFalse();
    }

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

    private static async Task<int> ReadResponseStartAsync(TcpClient client, Uri endpoint)
    {
        await client.ConnectAsync(endpoint.Host, endpoint.Port);
        using var stream = client.GetStream();
        await LoopbackHttpServer.WriteAsync(stream, RequestBytes(endpoint));
        return await LoopbackHttpServer.ReadAsync(stream, new byte[1]).WaitAsync(HangGuard.Bound, CancellationToken.None);
    }

    private static byte[] RequestBytes(Uri endpoint) =>
        Encoding.ASCII.GetBytes($"POST / HTTP/1.1\r\nHost: {endpoint.Authority}\r\nContent-Length: 0\r\n\r\n");
}
