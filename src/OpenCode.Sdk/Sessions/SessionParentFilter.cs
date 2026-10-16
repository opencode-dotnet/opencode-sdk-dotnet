namespace OpenCode.Sdk;

/// <summary>
/// Filters a session list by parent: a concrete parent's children, or root sessions only.
/// The wire spells root-only as the literal string <c>"null"</c>; this type keeps that
/// sentinel unrepresentable as ordinary data.
/// </summary>
public sealed record SessionParentFilter
{
    private const string RootOnlyWireValue = "null";

    private SessionParentFilter(string wireValue) => WireValue = wireValue;

    /// <summary>Gets the filter returning root sessions only.</summary>
    public static SessionParentFilter RootOnly { get; } = new(RootOnlyWireValue);

    /// <summary>Gets the raw wire value.</summary>
    internal string WireValue { get; }

    /// <summary>Creates a filter returning the children of one session.</summary>
    /// <param name="sessionId">The parent session identifier.</param>
    /// <returns>The filter.</returns>
    /// <exception cref="ArgumentException">The identifier is blank or spells the root-only sentinel.</exception>
    public static SessionParentFilter Of(string sessionId)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(sessionId);
        if (string.Equals(sessionId, RootOnlyWireValue, StringComparison.Ordinal))
        {
            throw new ArgumentException("The literal 'null' is the root-only sentinel; use RootOnly instead.", nameof(sessionId));
        }

        return new SessionParentFilter(sessionId);
    }

    /// <summary>Describes the root-only filter or the concrete parent identifier.</summary>
    /// <returns>A description naming RootOnly, or Of with its session identifier.</returns>
    public override string ToString() =>
        string.Equals(WireValue, RootOnlyWireValue, StringComparison.Ordinal)
            ? "SessionParentFilter { Kind = RootOnly }"
            : $"SessionParentFilter {{ Kind = Of, SessionId = {WireValue} }}";
}
