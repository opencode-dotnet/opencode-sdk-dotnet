using System.Globalization;
using System.Net;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Owns loopback admission for WebSocket upgrades across client families.</summary>
internal sealed class LoopbackUpgradeListener(Func<int>? reservePort = null) : IDisposable
{
    private HttpListener? _listener;

    public HttpListener Listener
    {
        get
        {
            if (_listener is null)
            {
                var ports = new List<string>();
                while (ports.Count < 5)
                {
                    var listener = new HttpListener();
                    try
                    {
                        var port = (reservePort ?? LoopbackPortReservation.Reserve)().ToString(CultureInfo.InvariantCulture);
                        ports.Add(port);
                        listener.Prefixes.Add($"http://127.0.0.1:{port}/");
                        listener.Start();
                        _listener = listener;
                        return _listener;
                    }
                    // 183: HTTP.sys prefix conflict; 32: TCP-held port. Probes miss HTTP.sys conflicts; ports can be taken after probing.
                    catch (HttpListenerException exception) when (OperatingSystem.IsWindows() && exception.ErrorCode is 183 or 32)
                    {
                        listener.Close();
                    }
                    catch
                    {
                        listener.Close();
                        throw;
                    }
                }

                throw new InvalidOperationException($"No loopback listener port could be registered: {string.Join(", ", ports)}.");
            }

            return _listener;
        }
    }

    public void Dispose() => _listener?.Close();
}
