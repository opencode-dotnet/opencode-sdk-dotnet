namespace OpenCode.Sdk;

/// <summary>
/// How <see cref="OpenCodeServer.EnsureAsync"/> treats a registered service whose version does not
/// match <see cref="OpenCodeServerEnsureOptions.ExpectedVersion"/>. The CLI's
/// <c>mismatch</c> flag at the pin: ignore, replace, or error.
/// </summary>
public enum OpenCodeServerVersionPolicy
{
    /// <summary>
    /// Strip the expected version before the election loop, so any ready compatible service is
    /// reused. The CLI default.
    /// </summary>
    Ignore = 0,

    /// <summary>
    /// Keep the expected version in the loop: a mismatch is announced and the registered service
    /// is replaced.
    /// </summary>
    Replace = 1,

    /// <summary>
    /// Discover twice outside the loop and throw when a ready service answers at the wrong version,
    /// without entering the election. A service that is not ready yet, or nothing registered, enters
    /// the election with the expected version kept, as <see cref="Replace"/> does.
    /// </summary>
    Error = 2,
}
