using System.Globalization;
using System.Net;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Experimental.Support;

/// <summary>Owns deterministic loopback admission and scripted client-MCP observations.</summary>
internal sealed class ClientMcpScenario : IDisposable
{
    public ClientMcpScenario()
    {
        Listener = new HttpListener();
        Listener.Prefixes.Add($"http://127.0.0.1:{LoopbackPortReservation.Reserve().ToString(CultureInfo.InvariantCulture)}/");
        Listener.Start();
    }

    public HttpListener Listener { get; }

    public void Dispose() => Listener.Close();
}
