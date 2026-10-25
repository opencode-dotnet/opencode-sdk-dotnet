using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The reconnect rule of the event bus, as the pinned client runs it: how long each attempt may
/// take, how long a connected stream may stay silent, the wait between attempts, and when a failing
/// stream gives up. Knowledge source: upstream-observed.
/// </summary>
/// <remarks>
/// Each attempt has <see cref="ConnectBound"/> to answer and deliver <c>server.connected</c> as its
/// first event, or it fails. A connected stream that receives no bytes for its idle window is cut —
/// <see cref="DefaultIdleWindow"/> unless the client options set another, as upstream's
/// <c>idleTimeout</c> option does — and the server's keepalive keeps a quiet stream alive. Every
/// failure, of any kind, is followed by a new attempt after <see cref="Delay"/>, with no backoff.
/// Upstream retries forever and shows a status; the SDK has no status to show, so a stream that has
/// failed every attempt for <see cref="GiveUp"/> — from the drop, or from the first attempt of a
/// first open — gives up and throws the last failure.
/// </remarks>
internal static class StreamReconnect
{
    /// <summary>The event that marks a connection as established, written first by the server on every connection.</summary>
    public const string ConnectedEventType = "server.connected";

    /// <summary>Gets the wait between a failure and the next attempt.</summary>
    public static TimeSpan Delay { get; } = TimeSpan.FromSeconds(1);

    /// <summary>Gets how long an attempt may take to answer and deliver its first event.</summary>
    public static TimeSpan ConnectBound { get; } = TimeSpan.FromSeconds(2);

    /// <summary>
    /// Gets how long a connected stream may go without receiving a byte when the client options set
    /// no other window.
    /// </summary>
    public static TimeSpan DefaultIdleWindow { get; } = TimeSpan.FromSeconds(45);

    /// <summary>Gets how long every attempt may keep failing before the stream gives up.</summary>
    public static TimeSpan GiveUp { get; } = TimeSpan.FromMinutes(1);

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
