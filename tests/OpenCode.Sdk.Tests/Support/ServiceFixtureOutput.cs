using System.Globalization;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The isolated discovery executable's output as a test reads it: the one stdout line for a found
/// service, the probe verdict it prints on stderr, the socket events of its timeline, and the
/// connects its probe transport made itself.
/// </summary>
internal static class ServiceFixtureOutput
{
    private const string ProbeTimedOutLine = "probe timedOut=true";
    private const string ProbeAnsweredLine = "probe timedOut=false";

    /// <summary>The timeline's socket event for a refused connect: <see cref="System.Net.Sockets.SocketError.ConnectionRefused"/> on every platform.</summary>
    private const string ConnectRefusedEvent = "System.Net.Sockets/ConnectFailed error=10061";

    /// <summary>The timeline's line for a connect the probe transport made itself, before whether the loopback option took on its socket.</summary>
    private const string LoopbackConnectPrefix = "loopback connect synRetransmissionDisabled=";

    public static string FoundLine(int processId, Uri endpoint) =>
        $"found owns=false pid={processId.ToString(CultureInfo.InvariantCulture)} endpoint={endpoint}";

    public static string EnsuredLine(int processId, Uri endpoint) =>
        $"ensured owns=false pid={processId.ToString(CultureInfo.InvariantCulture)} endpoint={endpoint}";

    /// <summary>
    /// Reads the probe verdict line: true when the probe's request bound expired, false when the
    /// probe classified an answer or a refusal, null when discovery never probed or the executable
    /// never reached the line.
    /// </summary>
    public static bool? ProbeTimedOut(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);

        if (standardError.Contains(ProbeTimedOutLine, StringComparison.Ordinal))
        {
            return true;
        }

        return standardError.Contains(ProbeAnsweredLine, StringComparison.Ordinal) ? false : null;
    }

    /// <summary>Reads whether the executable's timeline recorded a socket connect the peer refused.</summary>
    public static bool ConnectRefused(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);
        return standardError.Contains(ConnectRefusedEvent, StringComparison.Ordinal);
    }

    /// <summary>
    /// Reads every connect the executable's probe transport made itself, in order: whether the
    /// loopback option took on the socket it connected. Empty when the transport left every connect
    /// to the handler's default.
    /// </summary>
    public static IReadOnlyList<bool> LoopbackConnects(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);

        var connects = new List<bool>();
        foreach (var line in standardError.Split('\n'))
        {
            var start = line.IndexOf(LoopbackConnectPrefix, StringComparison.Ordinal);
            if (start >= 0)
            {
                connects.Add(line[(start + LoopbackConnectPrefix.Length)..].Trim() == "true");
            }
        }

        return connects;
    }
}
