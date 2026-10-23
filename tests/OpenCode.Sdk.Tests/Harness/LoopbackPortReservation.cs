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
        using var held = Hold();
        return held.Port;
    }

    /// <summary>
    /// Binds a free loopback port and keeps it bound until the returned hold is disposed, so the OS
    /// cannot hand the same port to another reservation while it is held.
    /// </summary>
    public static HeldLoopbackPort Hold()
    {
        var listener = new TcpListener(IPAddress.Loopback, 0);
        try
        {
            listener.Start();
            return new HeldLoopbackPort(((IPEndPoint)listener.LocalEndpoint).Port, () => Release(listener));
        }
        catch
        {
            Release(listener);
            throw;
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
        using var first = Hold();
        using var second = Hold();
        return (first.Port, second.Port);
    }

    private static void Release(TcpListener listener)
    {
        listener.Stop();
#if NET
        // TcpListener implements IDisposable from .NET 8 on; Stop() alone does not satisfy
        // CA2000 there. Downlevel targets have no Dispose — Stop() is their full release.
        listener.Dispose();
#endif
    }
}
