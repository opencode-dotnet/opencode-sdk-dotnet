using System.Collections.Frozen;

namespace OpenCode.Sdk.Internal;

/// <summary>
/// Decides the progress window one exchange runs under. A per-call value wins. Otherwise an
/// ordinary operation takes the client's value, or 100 seconds, the .NET convention. A long-poll
/// operation answers only when work ends: the server sends no headers until a session is idle, a
/// shell command exits, a command finishes its work, or a generation completes. A network window would cut a healthy wait
/// there, so a long poll runs with no window and the caller's token is its deadline, as upstream's
/// own client bounds it; a client-wide value does not reach it, only a per-call one does.
/// Knowledge source: upstream-observed.
/// </summary>
internal static class NetworkTimeouts
{
    private static readonly TimeSpan TimerCeiling = TimeSpan.FromMilliseconds(int.MaxValue);

    private static readonly FrozenSet<string> LongPollOperations = FrozenSet.ToFrozenSet(
        ["experimental.session.wait", "session.shell", "experimental.generate.text", "session.generate", "session.command"],
        StringComparer.Ordinal);

    /// <summary>Gets the operations that wait on work rather than on the network.</summary>
    public static FrozenSet<string> LongPolls => LongPollOperations;

    /// <summary>Gets the window an exchange runs under.</summary>
    /// <param name="operationId">The operation the exchange carries.</param>
    /// <param name="perCall">The call's own value, which wins.</param>
    /// <param name="client">The client's value, which ordinary operations take.</param>
    /// <returns>The window; <see cref="Timeout.InfiniteTimeSpan"/> for none.</returns>
    public static TimeSpan Resolve(string operationId, TimeSpan? perCall, TimeSpan? client) =>
        perCall ?? (LongPollOperations.Contains(operationId) ? Timeout.InfiniteTimeSpan : client ?? PipelineMessage.DefaultNetworkTimeout);

    /// <summary>
    /// Refuses a value no supported timer can run: zero, negative other than
    /// <see cref="Timeout.InfiniteTimeSpan"/>, or longer than <see cref="int.MaxValue"/> milliseconds.
    /// </summary>
    /// <param name="value">The value to check; <see langword="null"/> passes.</param>
    /// <param name="parameterName">The name the refusal reports.</param>
    /// <returns>The value unchanged.</returns>
    public static TimeSpan? Validate(TimeSpan? value, string parameterName)
    {
        if (value is not { } timeout || timeout == Timeout.InfiniteTimeSpan)
        {
            return value;
        }

        return timeout > TimeSpan.Zero && timeout <= TimerCeiling
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, timeout,
                "The network timeout must be above zero and at most int.MaxValue milliseconds, or Timeout.InfiniteTimeSpan.");
    }
}
