namespace OpenCode.Sdk;

/// <summary>Configures one opencode API call.</summary>
public sealed class OpenCodeRequestOptions
{
    /// <summary>
    /// Gets the headers validated and copied when this call starts; each cursor page is a separate
    /// call. The event subscription refuses them. Later values replace
    /// earlier ones: SDK User-Agent, <see cref="OpenCodeClientOptions.Headers"/>, SDK authentication,
    /// merged location and operation-declared headers, then these headers. Case-equivalent names
    /// in this set join in insertion order with ", ". Client construction refuses Authorization
    /// with a non-null <see cref="OpenCodeClientOptions.Password"/>, or x-opencode-directory with a
    /// non-null <see cref="OpenCodeClientOptions.Location"/>; these per-call headers allow both names.
    /// Content headers replace the content's headers when content exists and are dropped otherwise.
    /// Headers reach HTTP operations, stream opens and cursor pages, not terminal
    /// upgrades or background-service info probes. Invalid HTTP token names, null values, and values
    /// containing NUL (U+0000), CR, LF or any character above U+007F are refused with <see cref="ArgumentException"/>.
    /// </summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Gets the error channel for this call.</summary>
    public ErrorBehavior ErrorBehavior { get; init; }

    /// <summary>
    /// Gets the per-call location override. A set directory wins over the ambient
    /// <see cref="OpenCodeClientOptions.Location"/>; an unset one inherits it, and
    /// <see langword="null"/> uses the ambient location unmodified. Because
    /// <see cref="LocationSelector"/> refuses a blank directory, there is no way to clear the
    /// ambient directory for one call — only to leave it inherited or replace it.
    /// </summary>
    public LocationSelector? Location { get; init; }

    /// <summary>
    /// Gets how long this call may go without progress, which wins over
    /// <see cref="OpenCodeClientOptions.NetworkTimeout"/> and over an operation's own default; a
    /// long-poll operation, which runs with no window otherwise, is bounded by it too.
    /// <see langword="null"/> keeps the default. Accepts a value above zero and at most
    /// <see cref="int.MaxValue"/> milliseconds, or <see cref="Timeout.InfiniteTimeSpan"/> for no window.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative other than <see cref="Timeout.InfiniteTimeSpan"/>, or longer than <see cref="int.MaxValue"/> milliseconds.</exception>
    public TimeSpan? NetworkTimeout
    {
        get;
        init => field = Internal.TimerBounds.ValidateNetworkTimeout(value, nameof(value));
    }

    /// <summary>Gets a shared instance that returns API errors on the response envelope.</summary>
    public static OpenCodeRequestOptions NoThrow { get; } = new()
    {
        ErrorBehavior = ErrorBehavior.NoThrow,
    };
}
