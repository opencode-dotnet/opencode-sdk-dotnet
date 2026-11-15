namespace OpenCode.Sdk.Internal;

/// <summary>Reports an explicit version mismatch without owning a diagnostics channel.</summary>
internal static class VersionMismatchNotifier
{
    /// <summary>Invokes the optional callback once on an ordinal mismatch.</summary>
    /// <param name="endpoint">The explicit endpoint.</param>
    /// <param name="serverVersion">The probe's version.</param>
    /// <param name="callback">The optional synchronous callback.</param>
    public static void Notify(Uri endpoint, string serverVersion, Action<OpenCodeServerVersionMismatchContext>? callback)
    {
        if (!string.Equals(serverVersion, PinnedRelease.Version, StringComparison.Ordinal))
        {
            callback?.Invoke(new OpenCodeServerVersionMismatchContext(endpoint, serverVersion, PinnedRelease.Version));
        }
    }
}
