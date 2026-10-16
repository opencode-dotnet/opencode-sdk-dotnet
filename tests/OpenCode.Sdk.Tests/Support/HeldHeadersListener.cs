using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Owns a loopback listener for requests whose response headers the test withholds.</summary>
internal sealed class HeldHeadersListener : IDisposable
{
    private readonly TcpListener _listener = new(IPAddress.Loopback, 0);

    public HeldHeadersListener()
    {
        _listener.Start();
        var port = ((IPEndPoint)_listener.LocalEndpoint).Port.ToString(CultureInfo.InvariantCulture);
        Endpoint = new Uri($"http://127.0.0.1:{port}");
    }

    public Uri Endpoint { get; }

    public Task<TcpClient> AcceptAsync() => _listener.AcceptTcpClientAsync();

    public void Dispose()
    {
        _listener.Stop();
#if NET
        _listener.Dispose();
#endif
    }
}
