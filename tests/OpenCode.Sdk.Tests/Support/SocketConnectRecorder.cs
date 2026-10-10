#if NET
using System.Diagnostics.Tracing;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>
/// Records this process's socket connect events while it lives, so a test can read that its own
/// connect started and that the peer refused it, from the socket's own events rather than from the
/// answer of everything around it.
/// </summary>
internal sealed class SocketConnectRecorder : EventListener
{
    private const string SocketsSourceName = "System.Net.Sockets";
    private const string ConnectStart = "ConnectStart";
    private const string ConnectFailed = "ConnectFailed";
    private const string ErrorPayload = "error";

    /// <summary>Guards the two records; events arrive on whichever thread started or completed the connect.</summary>
    private readonly Lock _gate = new();

    private bool _refused;
    private int _connectStarts;

    /// <summary>
    /// How many connects this process started while the recorder lived. A test reads it to know
    /// <see cref="Refused"/> witnessed its own connect and no other.
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

    /// <summary>Gets whether a connect the peer refused followed a connect start.</summary>
    public bool Refused
    {
        get
        {
            lock (_gate)
            {
                return _refused;
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
        lock (_gate)
        {
            if (eventData.EventName == ConnectStart)
            {
                _connectStarts++;
            }
            else if (eventData.EventName == ConnectFailed && _connectStarts > 0 && IsRefusal(eventData))
            {
                _refused = true;
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
