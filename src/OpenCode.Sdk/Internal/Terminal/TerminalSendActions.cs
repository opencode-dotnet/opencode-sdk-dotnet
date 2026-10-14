namespace OpenCode.Sdk.Internal;

/// <summary>
/// Family-owned work that shares the socket's send ordering: prepare a message immediately
/// before its send, and publish local state only after that send succeeds.
/// </summary>
/// <param name="Prepare">Prepares the payload while holding the send gate.</param>
/// <param name="OnSent">Publishes state after a successful send, before releasing the gate.</param>
internal sealed record TerminalSendActions(Action? Prepare = null, Action? OnSent = null);
