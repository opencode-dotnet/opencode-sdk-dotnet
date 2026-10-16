using System.Net;
using System.Net.Sockets;

namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// Reserves a currently-free loopback port by binding port zero and releasing it. The window
/// between release and the server's own bind is a named residual risk of the drive manifest
/// contract, which requires explicit ports (manifest.ts:12-24); per-run instances keep the
/// exposure to one bind per suite run.
/// </summary>
internal static class LoopbackPortReservation
{
    public static int Reserve()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
#if NET
            // TcpListener implements IDisposable from .NET 8 on; Stop() alone does not satisfy
            // CA2000 there. Downlevel targets have no Dispose — Stop() is their full release.
            listener.Dispose();
#endif
        }
    }

    /// <summary>
    /// Reserves two distinct free loopback ports. Two <see cref="Reserve"/> calls cannot promise
    /// distinctness: the first listener is released before the second binds, so the OS is free to
    /// hand the very same ephemeral port straight back, which is what produced the equal-port
    /// failure observed in <c>DriveManifestTests</c>. Holding both listeners until both are bound
    /// makes the pair distinct by construction rather than by luck.
    /// </summary>
    public static (int First, int Second) ReservePair()
    {
        var first = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            first.Start();
            var second = new TcpListener(IPAddress.Loopback, 0);
            try
            {
                second.Start();
                return (((IPEndPoint)first.LocalEndpoint).Port, ((IPEndPoint)second.LocalEndpoint).Port);
            }
            finally
            {
                second.Stop();
#if NET
                second.Dispose();
#endif
            }
        }
        finally
        {
            first.Stop();
#if NET
            first.Dispose();
#endif
        }
    }
}
