#if NET
using System.Net;
using System.Net.Sockets;
using NSubstitute;
using NSubstitute.Extensions;
using OpenCode.Sdk.Internal;
using OpenCode.Sdk.Tests.Hosting.BackgroundService.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Transport;

public sealed class OwnedLoopbackTransportTests
{
    /// <summary>The owned loopback request takes the no-SYN-retransmission socket on Windows only.</summary>
    [Test]
    public async Task CreateOwnedHttpHandler_Should_Connect_Loopback_Through_The_Connector_Only_On_Windows()
    {
        await using var server = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.OK });
        var connector = new RecordingLoopbackConnector();
        using var handler = TransportPolicy.CreateOwnedHttpHandler(server.Endpoint, connector);
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, server.Endpoint);

        using var response = await invoker.SendAsync(request, CancellationToken.None);

        await Assert.That(response.StatusCode).IsEqualTo(HttpStatusCode.OK);
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(connector.Records).Count().IsEqualTo(1);
            await Assert.That(connector.Records.Single().SynRetransmissionDisabled).IsTrue();
        }
        else
        {
            await Assert.That(connector.Records).IsEmpty();
        }
    }

    [Test]
    public async Task CreateOwnedHttpHandler_Should_Keep_The_Default_Connect_For_A_Non_Loopback_Endpoint()
    {
        var connector = new RecordingLoopbackConnector();
        using var handler = (SocketsHttpHandler)TransportPolicy.CreateOwnedHttpHandler(new Uri("http://opencode.example"), connector);

        await Assert.That(handler.ConnectCallback is null).IsTrue();
        await Assert.That(connector.Records).IsEmpty();
    }

    /// <summary>A proxy connect keeps default SYN retransmissions even when the requested server is loopback.</summary>
    [Test]
    public async Task CreateOwnedHttpHandler_Should_Keep_Syn_Retransmission_For_A_Non_Loopback_Proxy()
    {
        var endpoint = new Uri("http://localhost:4096");
        var connector = new RefusingConnector();
        using var handler = (SocketsHttpHandler)TransportPolicy.CreateOwnedHttpHandler(endpoint, connector);
        handler.Proxy = new WebProxy("http://192.0.2.1:8080");
        handler.UseProxy = true;
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(handler.ConnectCallback is null).IsTrue();
            return;
        }

        _ = await FailedRequestAsync(handler, endpoint);

        await Assert.That(connector.Recorded).IsNotNull();
        // The assertion above proves the handler routed the proxy connect through the callback.
        await Assert.That(connector.Recorded!.EndPoint).IsEqualTo(new DnsEndPoint("192.0.2.1", 8080));
        await Assert.That(connector.Recorded.SynRetransmissionDisabled).IsFalse();
    }

    /// <summary>
    /// Every loopback form the handler hands its connect takes the option on Windows, bracketed IPv6
    /// and a scoped IPv6 literal included: <see cref="Uri.IsLoopback"/> counts each as loopback.
    /// </summary>
    [Test]
    [Arguments("http://127.0.0.1:4096/")]
    [Arguments("http://localhost:4096/")]
    [Arguments("http://[::1]:4096/")]
    [Arguments("http://[::1%1]:4096/")]
    [Arguments("http://[::ffff:127.0.0.1]:4096/")]
    public async Task CreateOwnedHttpHandler_Should_Disable_Syn_Retransmission_For_Every_Loopback_Target(string text)
    {
        var endpoint = new Uri(text);
        var connector = new RefusingConnector();
        using var handler = (SocketsHttpHandler)TransportPolicy.CreateOwnedHttpHandler(endpoint, connector);
        handler.UseProxy = false;
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(handler.ConnectCallback is null).IsTrue();
            return;
        }

        _ = await FailedRequestAsync(handler, endpoint);

        await Assert.That(endpoint.IsLoopback).IsTrue();
        await Assert.That(connector.Recorded).IsNotNull();
        await Assert.That(connector.Recorded!.SynRetransmissionDisabled).IsTrue();
    }

    /// <summary>Characterizes the real owned Windows handler's refusal chain, without making a timing claim.</summary>
    [Test]
    public async Task CreateOwnedHttpHandler_Should_Characterize_A_Closed_Windows_Loopback_Port_As_A_Refusal()
    {
        var endpoint = ClosedLoopbackEndpoint.Create();
        await Assert.That(endpoint.Port).IsLessThan(32768);
        using var handler = (SocketsHttpHandler)TransportPolicy.CreateOwnedHttpHandler(endpoint);
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(handler.ConnectCallback is null).IsTrue();
            return;
        }

        var exception = await FailedRequestAsync(handler, endpoint);

        var failures = new List<Exception>();
        for (Exception? failure = exception; failure is not null; failure = failure.InnerException)
        {
            failures.Add(failure);
        }

        await Assert.That(failures.OfType<SocketException>().Select(static failure => failure.SocketErrorCode))
            .Contains(SocketError.ConnectionRefused);
    }

    /// <summary>A cancelled connect releases its prepared socket before the owned callback returns the failure.</summary>
    [Test]
    public async Task CreateOwnedHttpHandler_Should_Dispose_The_Socket_When_Its_Connect_Is_Cancelled()
    {
        var endpoint = new Uri("http://127.0.0.1:4096");
        var connector = Substitute.For<ILoopbackSocketConnector>();
        Socket? socket = null;
        connector.ReturnsForAll(call =>
        {
            socket = call.Arg<LoopbackConnectAttempt>().Socket;
            return ValueTask.FromCanceled(new CancellationToken(canceled: true));
        });
        using var handler = (SocketsHttpHandler)TransportPolicy.CreateOwnedHttpHandler(endpoint, connector);
        if (!OperatingSystem.IsWindows())
        {
            await Assert.That(handler.ConnectCallback is null).IsTrue();
            return;
        }

        var exception = await FailedRequestAsync(handler, endpoint);

        await Assert.That(exception!.InnerException).IsAssignableTo<OperationCanceledException>();
        await Assert.That(socket).IsNotNull();
        // The assertion above proves the callback prepared a socket before it failed.
        await Assert.That(socket!.SafeHandle.IsClosed).IsTrue();
    }

    private static async Task<HttpRequestException?> FailedRequestAsync(HttpMessageHandler handler, Uri endpoint)
    {
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);
        return await Assert.That(async () =>
            {
                using var response = await invoker.SendAsync(request, CancellationToken.None);
            })
            .Throws<HttpRequestException>();
    }

    /// <summary>A connector that records the attempt it was handed and refuses it without connecting.</summary>
    private sealed class RefusingConnector : ILoopbackSocketConnector
    {
        /// <summary>Gets the last attempt the handler's connect handed over.</summary>
        public LoopbackConnectAttempt? Recorded { get; private set; }

        public ValueTask ConnectAsync(LoopbackConnectAttempt attempt, CancellationToken cancellationToken)
        {
            Recorded = attempt;
            return ValueTask.FromException(new SocketException((int)SocketError.ConnectionRefused));
        }
    }
}
#endif
