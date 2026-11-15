namespace OpenCode.Sdk;

/// <summary>A snapshot of the shared source; its error is a shared exception reference, not a frozen graph.</summary>
public sealed class EventConnectionState
{
    /// <summary>Initializes a connection snapshot, including snapshots supplied by consumer overrides.</summary>
    /// <param name="status">The connection phase.</param>
    /// <param name="attempt">The retry counter; SDK connected and idle snapshots carry zero.</param>
    /// <param name="error">The last classified attempt failure, or null.</param>
    public EventConnectionState(EventConnectionStatus status, int attempt, Exception? error)
    {
        Status = status;
        Attempt = attempt;
        Error = error;
    }

    /// <summary>Gets the source's current phase.</summary>
    public EventConnectionStatus Status { get; }

    /// <summary>Gets the retry counter, reset after a connection lived at least one second.</summary>
    public int Attempt { get; }

    /// <summary>Gets the last classified failure, retained through the next attempt and cleared on connection or idle.</summary>
    public Exception? Error { get; }
}
