using System.Net;
using System.Net.Sockets;
using OpenCode.Sdk.Internal.BackgroundService.Discovery;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.BackgroundService.Discovery;

/// <summary>
/// The probe transport's sealed policy, read off the handler it builds: a loopback endpoint is
/// never routed through a proxy (an environment proxy without <c>NO_PROXY</c> would otherwise hide
/// a live daemon), any other endpoint keeps the platform default, and no redirect is followed. On
/// the modern runtime a Windows loopback endpoint also connects through the transport's own
/// callback, on a socket that took the option disabling SYN retransmission; every other endpoint,
/// and every endpoint on another platform, keeps the handler's default connect. On the downlevel
/// targets the pre-connect runs on such a socket too, connects without the token, and reads its
/// own connect result: a refusal stands whatever the token says, and the token's cancellation
/// closes the socket and propagates.
/// </summary>
public sealed class LoopbackTransportTests
{
    [Test]
    [Arguments("http://127.0.0.1:4096/")]
    [Arguments("http://[::1]:4096/")]
    [Arguments("http://localhost:4096/")]
    public async Task CreateHandler_Should_Bypass_Every_Proxy_For_A_Loopback_Endpoint(string endpoint)
    {
        using var handler = PlatformTransport().CreateHandler(new Uri(endpoint));

        await Assert.That(UseProxy(handler)).IsFalse();
        await Assert.That(AllowAutoRedirect(handler)).IsFalse();
    }

    [Test]
    public async Task CreateHandler_Should_Keep_The_Proxy_Default_For_Another_Endpoint()
    {
        using var handler = PlatformTransport().CreateHandler(new Uri("http://opencode.example:4096/"));

        await Assert.That(UseProxy(handler)).IsTrue();
        await Assert.That(AllowAutoRedirect(handler)).IsFalse();
    }

#if NET
    [Test]
    [Arguments("http://127.0.0.1:4096/")]
    [Arguments("http://[::1]:4096/")]
    [Arguments("http://localhost:4096/")]
    public async Task CreateHandler_Should_Own_The_Loopback_Connect_Exactly_On_Windows(string endpoint)
    {
        using var handler = (SocketsHttpHandler)PlatformTransport().CreateHandler(new Uri(endpoint));

        await Assert.That(handler.ConnectCallback is not null).IsEqualTo(OperatingSystem.IsWindows());
    }

    [Test]
    public async Task CreateHandler_Should_Keep_The_Default_Connect_For_Another_Endpoint()
    {
        using var handler = (SocketsHttpHandler)PlatformTransport().CreateHandler(new Uri("http://opencode.example:4096/"));

        await Assert.That(handler.ConnectCallback is null).IsTrue();
    }

    /// <summary>
    /// The connect a Windows loopback exchange makes is the transport's own, on a socket the
    /// transport provider accepted the no-SYN-retransmission option on, and the closed port refuses
    /// it. Elsewhere the handler's default connect runs and the transport connects nothing itself.
    /// </summary>
    [Test]
    public async Task CreateHandler_Should_Connect_A_Windows_Loopback_Exchange_On_A_Socket_That_Took_The_Option()
    {
        var endpoint = await ClosedEndpointAsync();
        var connector = new RecordingLoopbackConnector();
        using var handler = new LoopbackTransport(connector).CreateHandler(endpoint);
        using var invoker = new HttpMessageInvoker(handler, disposeHandler: false);
        using var request = new HttpRequestMessage(HttpMethod.Get, endpoint);

        _ = await Assert
            .That(async () => _ = await invoker.SendAsync(request, CancellationToken.None))
            .Throws<HttpRequestException>();

        if (OperatingSystem.IsWindows())
        {
            var connect = connector.Records.Single();
            await Assert.That(connect.SynRetransmissionDisabled).IsTrue();
            await Assert.That(connect.Failure).IsEqualTo(SocketError.ConnectionRefused);
        }
        else
        {
            await Assert.That(connector.Records).IsEmpty();
        }
    }
#else
    /// <summary>
    /// The pre-connect runs on a socket the transport provider accepted the no-SYN-retransmission
    /// option on, so the closed port's refusal is the connect's own answer.
    /// </summary>
    [Test]
    public async Task IsListeningAsync_Should_Report_A_Refused_Loopback_Port_As_Not_Listening()
    {
        var endpoint = await ClosedEndpointAsync();
        var connector = new RecordingLoopbackConnector();

        var listening = await new LoopbackTransport(connector).IsListeningAsync(endpoint, CancellationToken.None);

        await Assert.That(listening).IsFalse();
        await Assert.That(connector.Records)
            .IsEquivalentTo([new LoopbackConnectRecord(OperatingSystem.IsWindows(), TokenCanBeCanceled: false, SocketError.ConnectionRefused)]);
    }

    [Test]
    public async Task IsListeningAsync_Should_Report_A_Listening_Loopback_Port()
    {
        await using var server = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.OK });

        var listening = await PlatformTransport().IsListeningAsync(server.Endpoint, CancellationToken.None);

        await Assert.That(listening).IsTrue();
    }

    /// <summary>
    /// The token never reaches the connect: the downlevel connect that takes one reports a
    /// cancellation in place of a refusal that completes while the token is being cancelled.
    /// </summary>
    [Test]
    public async Task IsListeningAsync_Should_Connect_Without_The_Token()
    {
        await using var server = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.OK });
        using var bound = new CancellationTokenSource();
        var connector = new RecordingLoopbackConnector();

        var listening = await new LoopbackTransport(connector).IsListeningAsync(server.Endpoint, bound.Token);

        await Assert.That(listening).IsTrue();
        await Assert.That(connector.Records.Single().TokenCanBeCanceled).IsFalse();
    }

    /// <summary>A refusal is the endpoint's own answer, so it stands when it completes while the token is being cancelled.</summary>
    [Test]
    public async Task IsListeningAsync_Should_Report_A_Refusal_That_Completes_While_The_Token_Is_Cancelled_As_Not_Listening()
    {
        var endpoint = await ClosedEndpointAsync();
        using var bound = new CancellationTokenSource();
        var connector = new ScriptedLoopbackConnector(bound, new SocketException((int)SocketError.ConnectionRefused));

        var listening = await new LoopbackTransport(connector).IsListeningAsync(endpoint, bound.Token);

        await Assert.That(listening).IsFalse();
    }

    /// <summary>
    /// A connect the token cut short fails the way the closed socket makes it fail, as an aborted
    /// connect or a disposed socket, and surfaces as the token's cancellation.
    /// </summary>
    [Test]
    [MethodDataSource(nameof(ClosedSocketFailures))]
    public async Task IsListeningAsync_Should_Throw_The_Token_Cancellation_For_A_Connect_The_Token_Cut_Short(Exception failure)
    {
        var endpoint = await ClosedEndpointAsync();
        using var bound = new CancellationTokenSource();
        var connector = new ScriptedLoopbackConnector(bound, failure);

        var exception = await Assert
            .That(async () => _ = await new LoopbackTransport(connector).IsListeningAsync(endpoint, bound.Token))
            .Throws<OperationCanceledException>();

        await Assert.That(exception!.CancellationToken).IsEqualTo(bound.Token);
    }

    /// <summary>
    /// The token closes the pre-connect's socket rather than reaching the connect, so a cancelled
    /// token surfaces as its own cancellation, not as a port that does not listen.
    /// </summary>
    [Test]
    public async Task IsListeningAsync_Should_Throw_The_Token_Cancellation_Instead_Of_Connecting()
    {
        await using var server = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.OK });
        var cancelled = new CancellationToken(canceled: true);

        var exception = await Assert
            .That(async () => _ = await PlatformTransport().IsListeningAsync(server.Endpoint, cancelled))
            .Throws<OperationCanceledException>();

        await Assert.That(exception!.CancellationToken).IsEqualTo(cancelled);
    }

    /// <summary>A refusal is the endpoint's own answer, so it stands when the token was cancelled meanwhile.</summary>
    [Test]
    public async Task ClassifyConnectFailure_Should_Report_A_Refusal_As_Not_Listening_Whatever_The_Token()
    {
        var cancelled = new CancellationToken(canceled: true);

        var listening = LoopbackTransport.ClassifyConnectFailure(new SocketException((int)SocketError.ConnectionRefused), cancelled);

        await Assert.That(listening).IsFalse();
    }

    /// <summary>
    /// Any failure other than a refusal under a cancelled token counts as the token's
    /// cancellation, as the token-taking connect reported it: the closed socket's aborted connect,
    /// and a failure the socket's closing did not cause, alike.
    /// </summary>
    [Test]
    [Arguments(SocketError.OperationAborted)]
    [Arguments(SocketError.HostNotFound)]
    public async Task ClassifyConnectFailure_Should_Throw_The_Token_Cancellation_For_Another_Failure(SocketError error)
    {
        var cancelled = new CancellationToken(canceled: true);

        var exception = await Assert
            .That(() => LoopbackTransport.ClassifyConnectFailure(new SocketException((int)error), cancelled))
            .Throws<OperationCanceledException>();

        await Assert.That(exception!.CancellationToken).IsEqualTo(cancelled);
    }

    [Test]
    [Arguments(SocketError.OperationAborted)]
    [Arguments(SocketError.HostNotFound)]
    public async Task ClassifyConnectFailure_Should_Report_Another_Failure_As_Not_Listening_While_The_Token_Stands(SocketError error)
    {
        var listening = LoopbackTransport.ClassifyConnectFailure(new SocketException((int)error), CancellationToken.None);

        await Assert.That(listening).IsFalse();
    }

    public static IEnumerable<Func<Exception>> ClosedSocketFailures() =>
    [
        static () => new SocketException((int)SocketError.OperationAborted),
        static () => new ObjectDisposedException(typeof(Socket).FullName),
    ];
#endif

    /// <summary>
    /// The Windows hosts this suite runs on (10 1709 and later) accept the loopback option; another
    /// platform is not asked, because the vendor control code throws there. Whether the kernel then
    /// skips the retransmissions is its documented behaviour, which no per-run assertion observes.
    /// </summary>
    [Test]
    public async Task DisableSynRetransmission_Should_Be_Accepted_On_Windows_And_Not_Asked_Elsewhere()
    {
        using var socket = new Socket(SocketType.Stream, ProtocolType.Tcp);

        var accepted = LoopbackTransport.DisableSynRetransmission(socket);

        await Assert.That(accepted).IsEqualTo(OperatingSystem.IsWindows());
    }

    private static LoopbackTransport PlatformTransport() => new(new LoopbackSocketConnector());

    /// <summary>A loopback port that was listening a moment ago and is closed now.</summary>
    private static async Task<Uri> ClosedEndpointAsync()
    {
        await using var server = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse { StatusCode = HttpStatusCode.OK });
        return server.Endpoint;
    }

    private static bool UseProxy(HttpMessageHandler handler) => handler switch
    {
#if NET
        SocketsHttpHandler sockets => sockets.UseProxy,
#endif
        HttpClientHandler client => client.UseProxy,
        _ => throw new InvalidOperationException("The probe handler is not a platform handler."),
    };

    private static bool AllowAutoRedirect(HttpMessageHandler handler) => handler switch
    {
#if NET
        SocketsHttpHandler sockets => sockets.AllowAutoRedirect,
#endif
        HttpClientHandler client => client.AllowAutoRedirect,
        _ => throw new InvalidOperationException("The probe handler is not a platform handler."),
    };
}
