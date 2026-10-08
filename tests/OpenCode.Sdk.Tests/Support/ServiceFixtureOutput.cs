using System.Globalization;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// The isolated discovery executable's output as a test reads it: the one stdout line for a found
/// service, the probe verdict it prints on stderr, and the socket events of its timeline.
/// </summary>
internal static class ServiceFixtureOutput
{
    private const string ProbeTimedOutLine = "probe timedOut=true";
    private const string ProbeAnsweredLine = "probe timedOut=false";

    /// <summary>The timeline's socket event for a refused connect: <see cref="System.Net.Sockets.SocketError.ConnectionRefused"/> on every platform.</summary>
    private const string ConnectRefusedEvent = "System.Net.Sockets/ConnectFailed error=10061";

    /// <summary>The timeline's socket event for a connect attempt starting.</summary>
    private const string ConnectStartEvent = "System.Net.Sockets/ConnectStart";

    /// <summary>What every timeline line starts with, before its milliseconds since the mode started.</summary>
    private const string TimelineStamp = "timeline +";

    private const string TimelineStampUnit = " ms ";

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
    /// Reads how long the refused connect took on the executable's own clock: from the socket's
    /// last connect start before the refusal to the refusal itself. Both stamps come from the same
    /// process, so the interval leaves out the executable's startup and every wait before the
    /// connect began.
    /// </summary>
    /// <returns>The interval, or null when the timeline holds no refused connect after a connect start.</returns>
    public static TimeSpan? ConnectRefusalTime(string standardError)
    {
        ArgumentNullException.ThrowIfNull(standardError);

        double? start = null;
        foreach (var line in standardError.Split('\n'))
        {
            if (line.Contains(ConnectStartEvent, StringComparison.Ordinal))
            {
                start = TimelineMilliseconds(line);
            }
            else if (line.Contains(ConnectRefusedEvent, StringComparison.Ordinal))
            {
                return start is { } started && TimelineMilliseconds(line) is { } refused
                    ? TimeSpan.FromMilliseconds(refused - started)
                    : null;
            }
        }

        return null;
    }

    /// <summary>Reads a timeline line's stamp, the milliseconds between <c>timeline +</c> and <c> ms </c>.</summary>
    private static double? TimelineMilliseconds(string line)
    {
        var start = line.IndexOf(TimelineStamp, StringComparison.Ordinal);
        if (start < 0)
        {
            return null;
        }

        start += TimelineStamp.Length;
        var end = line.IndexOf(TimelineStampUnit, start, StringComparison.Ordinal);
        return end > start
            && double.TryParse(line.AsSpan(start, end - start), NumberStyles.Float, CultureInfo.InvariantCulture, out var milliseconds)
            ? milliseconds
            : null;
    }
}
