using System.Globalization;
using System.Net;
using System.Net.Sockets;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>Selects closed loopback ports outside the ranges assigned to parallel servers.</summary>
internal static class ClosedLoopbackEndpoint
{
    /// <summary>The first port the closed endpoint tries; below every platform's ephemeral range.</summary>
    private const int FirstClosedPortCandidate = 20000;

    private const int ClosedPortCandidates = 1000;

    private static int s_lastClosedPortCandidate = FirstClosedPortCandidate - 1;

    /// <summary>
    /// A loopback port nothing listens on, found free by a bind and released. It lies below every
    /// platform's ephemeral range (Windows and macOS from 49152, Linux from 32768), so no parallel
    /// test binding port 0 can be handed it and turn the refusal into a connect. A port a disposed
    /// server had just released from that range could be (#421). Every call takes its own
    /// candidates, so a parallel caller is never binding the port this one connects to: macOS times
    /// a bound, non-listening port out instead of refusing it.
    /// </summary>
    public static Uri Create()
    {
        for (var attempt = 0; attempt < ClosedPortCandidates; attempt++)
        {
            var port = Interlocked.Increment(ref s_lastClosedPortCandidate);
            if (IsFree(port))
            {
                return new Uri($"http://127.0.0.1:{port.ToString(CultureInfo.InvariantCulture)}");
            }
        }

        throw new InvalidOperationException("No free loopback port below the ephemeral ranges was found for the closed endpoint.");
    }

    /// <summary>Whether a bind to the loopback port succeeds; the probe socket is released at once.</summary>
    private static bool IsFree(int port)
    {
        using var probe = new Socket(AddressFamily.InterNetwork, SocketType.Stream, ProtocolType.Tcp);
        try
        {
            probe.Bind(new IPEndPoint(IPAddress.Loopback, port));
            return true;
        }
        catch (SocketException exception) when (exception.SocketErrorCode is SocketError.AddressAlreadyInUse or SocketError.AccessDenied)
        {
            // Taken, or inside a range Windows has excluded (WinNAT, Hyper-V); any other fault propagates.
            return false;
        }
    }
}
