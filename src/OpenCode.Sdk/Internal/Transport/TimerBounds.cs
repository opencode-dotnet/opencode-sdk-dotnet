namespace OpenCode.Sdk.Internal;

/// <summary>
/// The range every supported timer can run, and the two window checks built on it: the network
/// timeout admits <see cref="Timeout.InfiniteTimeSpan"/>, the event stream idle window does not.
/// </summary>
internal static class TimerBounds
{
    /// <summary>Gets the longest duration every supported timer can represent.</summary>
    public static TimeSpan TimerCeiling { get; } = TimeSpan.FromMilliseconds(int.MaxValue);

    /// <summary>
    /// Refuses a network timeout no supported timer can run: zero, negative other than
    /// <see cref="Timeout.InfiniteTimeSpan"/>, or longer than <see cref="TimerCeiling"/>.
    /// </summary>
    /// <param name="value">The value to check; <see langword="null"/> passes.</param>
    /// <param name="parameterName">The name the refusal reports.</param>
    /// <returns>The value unchanged.</returns>
    public static TimeSpan? ValidateNetworkTimeout(TimeSpan? value, string parameterName) =>
        value is not { } timeout || timeout == Timeout.InfiniteTimeSpan || IsWithinTimerRange(timeout)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, timeout,
                "The network timeout must be above zero and at most int.MaxValue milliseconds, or Timeout.InfiniteTimeSpan.");

    /// <summary>
    /// Admits an idle window: unset, or above zero and at most <see cref="TimerCeiling"/>.
    /// Upstream's client has no window that never ends, so neither does this one.
    /// </summary>
    /// <param name="value">The window to admit.</param>
    /// <param name="parameterName">The name the refusal reports.</param>
    /// <returns>The admitted window.</returns>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The window is zero, negative, infinite, or longer than <see cref="TimerCeiling"/>.
    /// </exception>
    public static TimeSpan? ValidateIdleWindow(TimeSpan? value, string parameterName) =>
        value is not { } window || IsWithinTimerRange(window)
            ? value
            : throw new ArgumentOutOfRangeException(parameterName, window,
                "The event stream idle window must be above zero and at most int.MaxValue milliseconds.");

    private static bool IsWithinTimerRange(TimeSpan value) => value > TimeSpan.Zero && value <= TimerCeiling;
}
