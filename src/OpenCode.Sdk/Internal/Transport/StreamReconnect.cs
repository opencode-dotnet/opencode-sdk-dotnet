using System.Collections.Frozen;
using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The reconnect rule of the event bus, as the pinned client runs it. Each attempt has 2 seconds to
/// answer and deliver <c>server.connected</c> as its first event, or it fails. A connected stream
/// that receives no bytes for 45 seconds is cut, and the server's keepalive every 15 seconds keeps a
/// quiet stream alive. Every failure, of any kind, is followed by a new attempt one second later,
/// with no backoff. Upstream retries forever and shows a status; the SDK has no status to show, so
/// a stream that has failed every attempt for one minute — from the drop, or from the first attempt
/// of a first open — gives up and throws the last failure. Knowledge source: upstream-observed.
/// </summary>
internal static class StreamReconnect
{
    /// <summary>The event that marks a connection as established, written first by the server on every connection.</summary>
    public const string ConnectedEventType = "server.connected";

    private static readonly FrozenSet<string> ReconnectingOperations = FrozenSet.ToFrozenSet(["event.subscribe"], StringComparer.Ordinal);

    /// <summary>Gets the wait between a failure and the next attempt.</summary>
    public static TimeSpan Delay { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets how long an attempt may take to answer and deliver its first event.</summary>
    public static TimeSpan ConnectBound { get; } = TimeSpan.FromSeconds(2);

    /// <summary>Gets how long a connected stream may go without receiving a byte.</summary>
    public static TimeSpan IdleWindow { get; } = TimeSpan.FromSeconds(45);

    /// <summary>Gets how long every attempt may keep failing before the stream gives up.</summary>
    public static TimeSpan GiveUp { get; } = TimeSpan.FromMinutes(1);

    /// <summary>Gets the operations whose stream reconnects instead of ending with its connection.</summary>
    public static FrozenSet<string> Reconnecting => ReconnectingOperations;

    /// <summary>
    /// Gets whether a frame establishes the connection that delivered it: an unnamed frame whose
    /// payload is an object with <c>type</c> <c>server.connected</c>. The frame is read as the wire
    /// wrote it, so the transport needs nothing from the generated models.
    /// </summary>
    /// <param name="frame">The first frame a connection delivered.</param>
    /// <returns><see langword="true"/> for <c>server.connected</c>.</returns>
    public static bool IsConnected(ServerSentEvent frame)
    {
        if (!string.Equals(frame.Name, ServerSentEvent.DefaultName, StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            using var document = JsonDocument.Parse(frame.Data);
            return document.RootElement.ValueKind is JsonValueKind.Object
                   && document.RootElement.TryGetProperty("type", out var type)
                   && type.ValueKind is JsonValueKind.String
                   && type.ValueEquals(ConnectedEventType);
        }
        catch (JsonException)
        {
            return false;
        }
    }
}
