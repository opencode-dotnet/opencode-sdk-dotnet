#if NET
using System.Diagnostics;
using System.Diagnostics.Tracing;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// Records this process's socket connect events while it lives, stamped on the writing thread the
/// moment the runtime raises them, so a test can read how long a refused connect took from the
/// connect's own start rather than from the start of everything around it.
/// </summary>
internal sealed class SocketConnectRecorder : EventListener
{
    private const string SocketsSourceName = "System.Net.Sockets";
    private const string ConnectStart = "ConnectStart";
    private const string ConnectFailed = "ConnectFailed";
    private const string ErrorPayload = "error";

    /// <summary>Guards the two stamps; events arrive on whichever thread started or completed the connect.</summary>
    private readonly Lock _gate = new();

    private long? _lastStart;
    private TimeSpan? _refusalTime;
    private int _connectStarts;

    /// <summary>
    /// How many connects this process started while the recorder lived. A test reads it to know
    /// <see cref="RefusalTime"/> measured its own connect: a second connect starting during a slow
    /// refusal would restart the measured interval and shorten it.
    /// </summary>
    public int ConnectStarts
    {
        get
        {
            lock (_gate)
            {
                return _connectStarts;
            }
        }
    }

    /// <summary>
    /// The interval from the last connect start to the first connect the peer refused after it,
    /// or null while no refused connect has followed a connect start.
    /// </summary>
    public TimeSpan? RefusalTime
    {
        get
        {
            lock (_gate)
            {
                return _refusalTime;
            }
        }
    }

    protected override void OnEventSourceCreated(EventSource eventSource)
    {
        ArgumentNullException.ThrowIfNull(eventSource);
        if (eventSource.Name == SocketsSourceName)
        {
            EnableEvents(eventSource, EventLevel.Informational);
        }
    }

    protected override void OnEventWritten(EventWrittenEventArgs eventData)
    {
        ArgumentNullException.ThrowIfNull(eventData);
        var now = Stopwatch.GetTimestamp();
        lock (_gate)
        {
            if (eventData.EventName == ConnectStart)
            {
                _lastStart = now;
                _connectStarts++;
            }
            else if (eventData.EventName == ConnectFailed
                && _refusalTime is null
                && _lastStart is { } started
                && IsRefusal(eventData))
            {
                _refusalTime = Stopwatch.GetElapsedTime(started, now);
            }
        }
    }

    private static bool IsRefusal(EventWrittenEventArgs eventData)
    {
        var index = eventData.PayloadNames?.IndexOf(ErrorPayload) ?? -1;
        return index >= 0
            && eventData.Payload is { } values
            && index < values.Count
            && values[index] is int error
            && error == (int)System.Net.Sockets.SocketError.ConnectionRefused;
    }
}
#endif
