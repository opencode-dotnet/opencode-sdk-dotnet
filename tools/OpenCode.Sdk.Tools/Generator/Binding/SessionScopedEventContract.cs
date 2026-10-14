namespace OpenCode.Sdk.Tools.Generator.Binding;

/// <summary>
/// The hand-written contract a session-scoped event implements. The names are fixed by the
/// hand-written interface, so they live here rather than in curation; the reserved-name policy
/// keeps every generated type off the interface's name.
/// </summary>
internal static class SessionScopedEventContract
{
    public const string InterfaceName = "ISessionScopedEvent";

    public const string MemberName = "SessionId";
}
