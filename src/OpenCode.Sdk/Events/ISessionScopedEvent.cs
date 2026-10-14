namespace OpenCode.Sdk.Models;

/// <summary>An event that belongs to one session: its payload names the session it happened in.</summary>
/// <remarks>Route a session-scoped event with <c>if (e is ISessionScopedEvent { SessionId: var id })</c>.</remarks>
public interface ISessionScopedEvent
{
    /// <summary>Gets the id of the session the event belongs to.</summary>
    public string SessionId { get; }
}
