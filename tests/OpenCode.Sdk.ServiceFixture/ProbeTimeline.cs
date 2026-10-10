using System.Collections.Concurrent;
using System.Diagnostics;
using System.Diagnostics.Tracing;
using System.Globalization;
using System.Text;

namespace OpenCode.Sdk.ServiceFixture;

/// <summary>
/// The discovery mode's diagnostic timeline: the runtime's own network events (HTTP request,
/// socket connect, name resolution) and the probe's start and end, each stamped with the
/// milliseconds since this process reached the mode, followed by the process's startup time and
/// CPU time. A discovery test prints the executable's stderr when it fails, so a probe that spent
/// its bound shows where the time went: a connect that started late (a starved process), a refusal
/// that arrived late (the SYN retransmissions the loopback option removes), or a connect that
/// succeeded against a port that was no longer closed.
/// </summary>
/// <remarks>
/// The lines are diagnostics, with two exceptions the stale-registration test reads: the socket's
/// refused-connect event, the witness of the refusal, and the line the probe transport's own
/// connect marks (<see cref="LoopbackConnectLine"/>), the witness that the socket it connected took
/// the loopback option. Each event is stamped the moment the listener receives it, before its
/// payload is rendered. No line contains the verdict text <c>probe timedOut=</c> the tests read.
/// Every line starts with <c>timeline</c>.
/// </remarks>
internal sealed class ProbeTimeline : EventListener
{
    private const string HttpSourceName = "System.Net.Http";

    /// <summary>
    /// Every keyword except <c>System.Net.Http</c>'s keyword 1, the one that gates
    /// <c>RequestFailedDetailed</c>. That event's payload is the failure's full <c>ToString()</c>, which
    /// <c>HttpClient</c> renders inside its own failure path, after the refusal and before the
    /// exception reaches the probe. On a starved process the rendering took hundreds of milliseconds
    /// to over a second, all of it spent inside the probe's exchange.
    /// Events without keywords, which are all the others this timeline reads, stay enabled.
    /// </summary>
    private const EventKeywords AllButRequestFailedDetailed = (EventKeywords)~1L;

    private const string AddressPayload = "address";

    private static readonly string[] SourceNames = [HttpSourceName, "System.Net.Sockets", "System.Net.NameResolution"];

    /// <summary>
    /// The moment the mode started. Like <see cref="_lines"/> it is a field initializer, which runs
    /// before the base constructor reports the event sources that already exist, so both are ready
    /// for the first <see cref="OnEventWritten"/> call.
    /// </summary>
    private readonly long _origin = Stopwatch.GetTimestamp();

    /// <summary>How long the operating system took from creating this process to the mode's start.</summary>
    private readonly TimeSpan _startup = StartupTime();

    private readonly ConcurrentQueue<string> _lines = new();

    /// <summary>What the line for a connect of the probe transport's own starts with, before <c>true</c> or <c>false</c>.</summary>
    private const string LoopbackConnectPrefix = "loopback connect synRetransmissionDisabled=";

    /// <summary>The line for a connect of the probe transport's own, naming whether the loopback option took on its socket.</summary>
    /// <param name="synRetransmissionDisabled">Whether the transport provider accepted the option.</param>
    /// <returns>The line's text.</returns>
    public static string LoopbackConnectLine(bool synRetransmissionDisabled) =>
        LoopbackConnectPrefix + (synRetransmissionDisabled ? "true" : "false");

    /// <summary>Records a named moment of the discovery mode itself.</summary>
    /// <param name="text">What happened.</param>
    public void Mark(string text) => _lines.Enqueue(Line(Stopwatch.GetElapsedTime(_origin), text));

    /// <summary>Renders every recorded line, then the process's startup and CPU time.</summary>
    /// <returns>The timeline, one line per event.</returns>
    public string Render()
    {
        using var process = Process.GetCurrentProcess();
        var elapsed = Stopwatch.GetElapsedTime(_origin);
        var builder = new StringBuilder();
        foreach (var line in _lines)
        {
            _ = builder.AppendLine(line);
        }

        _ = builder
            .Append("timeline process startup=")
            .Append(Milliseconds(_startup))
            .Append(" ms cpu=")
            .Append(Milliseconds(process.TotalProcessorTime))
            .Append(" ms wall=")
            .Append(Milliseconds(elapsed))
            .Append(" ms cores=")
            .Append(Environment.ProcessorCount.ToString(CultureInfo.InvariantCulture));
        return builder.ToString();
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        ArgumentNullException.ThrowIfNull(eventSource);
        if (Array.IndexOf(SourceNames, eventSource.Name) >= 0)
        {
            var keywords = eventSource.Name == HttpSourceName ? AllButRequestFailedDetailed : EventKeywords.None;
            EnableEvents(eventSource, EventLevel.Informational, keywords);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        var stamp = Stopwatch.GetElapsedTime(_origin);

        var text = new StringBuilder()
            .Append(eventData.EventSource.Name)
            .Append('/')
            .Append(eventData.EventName);
        if (eventData.PayloadNames is { } names && eventData.Payload is { } values)
        {
            for (var index = 0; index < names.Count && index < values.Count; index++)
            {
                // ConnectStart's address is a serialized socket address, which rendered as raw bytes
                // on a CI runner; the probe start line already names the endpoint in readable form.
                if (names[index] == AddressPayload)
                {
                    continue;
                }

                _ = text
                    .Append(' ')
                    .Append(names[index])
                    .Append('=')
                    .Append(Convert.ToString(values[index], CultureInfo.InvariantCulture));
            }
        }

        _lines.Enqueue(Line(stamp, text.ToString()));
    }

    /// <summary>The wall-clock gap between the process's creation and now: the process start time is a wall-clock value, so no monotonic clock can measure it.</summary>
    private static TimeSpan StartupTime()
    {
        using var process = Process.GetCurrentProcess();
        return DateTime.UtcNow - process.StartTime.ToUniversalTime();
    }

    private static string Milliseconds(TimeSpan span) =>
        span.TotalMilliseconds.ToString("F1", CultureInfo.InvariantCulture);

    private static string Line(TimeSpan stamp, string text) =>
        "timeline +" + Milliseconds(stamp) + " ms " + text;
}
