namespace OpenCode.Sdk.Internal;

/// <summary>
/// Family-owned work that shares the socket's send ordering: prepare a message immediately
/// before its send, and publish local state only after that send succeeds.
/// </summary>
/// <param name="Prepare">Prepares the payload while holding the send gate.</param>
/// <param name="OnSent">Publishes state after a successful send, before releasing the gate.</param>
/// <param name="PayloadRented">createPayload returned an offset-zero segment over an array rented from
/// the core's SendPool; the core owns that array from then on.</param>
internal sealed record TerminalSendActions(Action<ArraySegment<byte>>? Prepare = null, Action? OnSent = null, bool PayloadRented = false)
{
    /// <summary>Gets shared actions for an owned rented payload with no family-specific work.</summary>
    public static TerminalSendActions Rented { get; } = new(PayloadRented: true);
}
