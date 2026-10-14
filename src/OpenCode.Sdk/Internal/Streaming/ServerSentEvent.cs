namespace OpenCode.Sdk.Internal;

/// <summary>
/// One dispatched server-sent event: the payload its <c>data</c> fields carry, and the name
/// that identifies what the frame is. The wire omits <c>event:</c> for an ordinary payload,
/// so an unnamed frame carries <see cref="DefaultName"/> — which is what makes a named frame
/// a signal rather than a payload.
/// </summary>
/// <param name="Name">The event name, defaulting to <see cref="DefaultName"/> on the wire.</param>
/// <param name="Data">The joined value of the frame's <c>data</c> fields.</param>
internal readonly record struct ServerSentEvent(string Name, string Data)
{
    /// <summary>The name the SSE grammar gives a frame that carries no <c>event</c> field.</summary>
    public const string DefaultName = "message";
}
