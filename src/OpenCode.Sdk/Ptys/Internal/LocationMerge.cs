namespace OpenCode.Sdk.Internal;

/// <summary>
/// The member-by-member location merge, stated once: a set per-call member always wins, an unset
/// (null) member inherits the ambient value unchanged, and — because
/// <see cref="LocationSelector"/> refuses blank members — there is no spelling that clears an
/// ambient member for one call.
/// </summary>
/// <remarks>
/// <see cref="RequestDecorationPolicy"/> applies the same rule on the header channel, but fuses
/// the directory member's percent-encoding into the same expression so a request that inherits
/// the ambient directory reuses the escape computed once at construction. That fusion is a hot-path
/// property of the header channel, not a second rule; this helper is the query-channel form the
/// PTY connect door uses.
/// </remarks>
internal static class LocationMerge
{
    /// <summary>Resolves the location one call addresses; null when neither side sets a member.</summary>
    /// <param name="perCall">The per-call selector, or null when the call sets none.</param>
    /// <param name="ambient">The client's ambient selector, or null when the client sets none.</param>
    /// <returns>The merged selector, or null when the merge resolves no member at all.</returns>
    public static LocationSelector? Resolve(LocationSelector? perCall, LocationSelector? ambient)
    {
        // A merge with nothing to merge is the common case; neither side is copied for it.
        if (perCall is null)
        {
            return ambient;
        }

        if (ambient is null)
        {
            return perCall;
        }

        var directory = perCall.Directory ?? ambient.Directory;

        // Nothing was inherited, so the per-call selector already is the merge.
        if (ReferenceEquals(directory, perCall.Directory))
        {
            return perCall;
        }

        return new LocationSelector
        {
            Directory = directory,
        };
    }
}
