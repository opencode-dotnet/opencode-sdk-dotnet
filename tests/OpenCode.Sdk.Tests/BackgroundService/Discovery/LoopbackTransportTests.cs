using OpenCode.Sdk.Internal.BackgroundService.Discovery;
#if !NET
using OpenCode.Sdk.Tests.Support;
#endif

namespace OpenCode.Sdk.Tests.BackgroundService.Discovery;

/// <summary>
/// The probe transport's sealed policy, read off the handler it builds: a loopback endpoint is
/// never routed through a proxy (an environment proxy without <c>NO_PROXY</c> would otherwise hide
/// a live daemon), any other endpoint keeps the platform default, and no redirect is followed. On
/// the modern runtime a Windows loopback endpoint also connects through the transport's own
/// callback, the one that disables SYN retransmission before connecting; every other endpoint, and
/// every endpoint on another platform, keeps the handler's default connect.
/// </summary>
public sealed class LoopbackTransportTests
{
    [Test]
    [Arguments("http://127.0.0.1:4096/")]
    [Arguments("http://[::1]:4096/")]
    [Arguments("http://localhost:4096/")]
    public async Task CreateProbeHandler_Should_Bypass_Every_Proxy_For_A_Loopback_Endpoint(string endpoint)
    {
        using var handler = LoopbackTransport.CreateProbeHandler(new Uri(endpoint));

        await Assert.That(UseProxy(handler)).IsFalse();
        await Assert.That(AllowAutoRedirect(handler)).IsFalse();
    }

    [Test]
    public async Task CreateProbeHandler_Should_Keep_The_Proxy_Default_For_Another_Endpoint()
    {
        using var handler = LoopbackTransport.CreateProbeHandler(new Uri("http://opencode.example:4096/"));

        await Assert.That(UseProxy(handler)).IsTrue();
        await Assert.That(AllowAutoRedirect(handler)).IsFalse();
    }

#if NET
    [Test]
    [Arguments("http://127.0.0.1:4096/")]
    [Arguments("http://[::1]:4096/")]
    [Arguments("http://localhost:4096/")]
    public async Task CreateProbeHandler_Should_Own_The_Loopback_Connect_Exactly_On_Windows(string endpoint)
    {
        using var handler = (SocketsHttpHandler)LoopbackTransport.CreateProbeHandler(new Uri(endpoint));

        await Assert.That(handler.ConnectCallback is not null).IsEqualTo(OperatingSystem.IsWindows());
    }

    [Test]
    public async Task CreateProbeHandler_Should_Keep_The_Default_Connect_For_Another_Endpoint()
    {
        using var handler = (SocketsHttpHandler)LoopbackTransport.CreateProbeHandler(new Uri("http://opencode.example:4096/"));

        await Assert.That(handler.ConnectCallback is null).IsTrue();
    }
#else
    /// <summary>
    /// The downlevel transport's pre-connect learns a refused loopback port without the SYN
    /// retransmissions Windows otherwise spends on it, about two seconds. The runtime raises no
    /// socket events here, so the interval is the pre-connect's whole call: the socket's creation,
    /// the option, the connect, and the wait for a thread to resume on. Load only lengthens it, so
    /// no load carries a pre-connect without the option under the line. Other platforms take no
    /// option and refuse at once, so there the interval guards nothing and stays unchecked.
    /// </summary>
    [Test]
    [NotInParallel]
    public async Task IsListeningAsync_Should_Learn_A_Refused_Loopback_Port_Without_Syn_Retransmissions()
    {
        Uri endpoint;
        await using (var server = LoopbackHttpServer.Start(static _ => new LoopbackHttpResponse { StatusCode = System.Net.HttpStatusCode.OK }))
        {
            endpoint = server.Endpoint;
        }

        var stopwatch = System.Diagnostics.Stopwatch.StartNew();
        var listening = await LoopbackTransport.IsListeningAsync(endpoint, CancellationToken.None);
        var elapsed = stopwatch.Elapsed;

        await Assert.That(listening).IsFalse();
        if (OperatingSystem.IsWindows())
        {
            await Assert.That(elapsed).IsLessThan(ServiceTimingData.RefusalWithoutRetransmission);
        }
    }
#endif

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
