using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// The normal PTY family's close policy: the seam form of <see cref="PtyCloseFailurePolicy"/>,
/// holding no state of its own, so one shared instance serves every session.
/// </summary>
internal sealed class PtyClosePolicy : ITerminalClosePolicy
{
    private PtyClosePolicy()
    {
    }

    /// <summary>Gets the shared policy instance.</summary>
    public static PtyClosePolicy Instance { get; } = new();

    /// <inheritdoc />
    public OpenCodeTransportException? Map(WebSocketCloseStatus? status, string? description) =>
        PtyCloseFailurePolicy.Map(status, description);
}
