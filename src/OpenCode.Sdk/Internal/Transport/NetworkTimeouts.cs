namespace OpenCode.Sdk.Internal;

/// <summary>
/// Decides the progress window one exchange runs under: a per-call value wins; otherwise an ordinary
/// operation takes the client's value or <see cref="DefaultNetworkTimeout"/>, and a long poll runs
/// with no window. Knowledge source: upstream-observed.
/// </summary>
/// <remarks>
/// The default is the .NET convention. A long-poll operation answers only when work ends: the server
/// sends no headers until a session is idle, a shell command exits, a command finishes its work, or a
/// generation completes. A network window would cut a healthy wait there, so a long poll runs with no
/// window and the caller's token is its deadline, as upstream's own client bounds it; a client-wide
/// value does not reach it, only a per-call one does.
/// </remarks>
internal static class NetworkTimeouts
{
    /// <summary>The progress window an ordinary exchange uses when neither call nor client supplies one.</summary>
    internal static readonly TimeSpan DefaultNetworkTimeout = TimeSpan.FromSeconds(100);

    /// <summary>Gets the window an exchange runs under.</summary>
    /// <param name="traits">The operation rules carried by the call's description.</param>
    /// <param name="perCall">The call's own value, which wins.</param>
    /// <param name="client">The client's value, which ordinary operations take.</param>
    /// <returns>The window; <see cref="Timeout.InfiniteTimeSpan"/> for none.</returns>
    public static TimeSpan Resolve(OperationTraits traits, TimeSpan? perCall, TimeSpan? client) =>
        perCall ?? (traits.HasNoNetworkWindow ? Timeout.InfiniteTimeSpan : client ?? DefaultNetworkTimeout);
}
