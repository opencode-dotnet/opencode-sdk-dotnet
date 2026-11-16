namespace OpenCode.Sdk;

/// <summary>
/// Configures an opencode client for its whole lifetime. The class stays settable for the
/// options and configuration-binding patterns; a client snapshots it at construction, so later
/// mutation never reaches a built client.
/// </summary>
public sealed class OpenCodeClientOptions
{
    /// <summary>
    /// Gets the case-insensitive headers copied and validated once at client construction.
    /// Later values replace earlier ones: SDK User-Agent, these headers, SDK authentication,
    /// merged location and operation-declared headers, then <see cref="OpenCodeRequestOptions.Headers"/>.
    /// Authorization with a non-null <see cref="Password"/>, or x-opencode-directory with a
    /// non-null <see cref="Location"/>, is refused. Content headers replace the content's headers
    /// when content exists and are dropped otherwise. Headers reach HTTP operations, stream opens
    /// (each shared event feed attempt included) and cursor pages, not terminal upgrades or
    /// background-service info probes.
    /// Invalid HTTP token names, null values, and values containing NUL (U+0000), CR, LF or any
    /// character above U+007F are refused with <see cref="ArgumentException"/>.
    /// </summary>
    public IDictionary<string, string> Headers { get; } = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);

    /// <summary>Gets or sets the absolute HTTP or HTTPS server endpoint; required to build a client.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the handler read once at construction for every HTTP request and event-stream
    /// connection. The default, <see langword="null"/>, lets the SDK build and own its handler.
    /// A supplied handler belongs to the caller and is never disposed by the SDK.
    /// The SDK does not apply its owned-handler settings to it: redirects disabled, a 120-second
    /// connection lifetime, a 3-second idle timeout, Windows loopback connect, or downlevel
    /// ServicePoint configuration. The first three settings are recommended; a 3xx response
    /// returned by the handler is still refused.
    /// Terminal upgrades use this handler on net10.0 and net11.0; net472 and netstandard2.0 keep
    /// the SDK's own socket because their ClientWebSocket cannot take a handler.
    /// </summary>
    public HttpMessageHandler? HttpHandler { get; set; }

    /// <summary>
    /// Gets or sets the HTTP basic-authentication username; the default is
    /// <c>opencode</c>, the only username the pinned server accepts.
    /// </summary>
    public string Username { get; set; } = "opencode";

    /// <summary>
    /// Gets or sets the HTTP basic-authentication password. Every server the <c>opencode</c>
    /// CLI starts requires one — the value set through <c>OPENCODE_PASSWORD</c>, or the one the
    /// CLI generates and prints as <c>server password &lt;pw&gt;</c> when none is configured.
    /// A <see langword="null"/> value sends no credential of the SDK's own. Use it for a host without
    /// authentication, such as an embedded server, or supply Authorization through <see cref="Headers"/>
    /// or <see cref="OpenCodeRequestOptions.Headers"/>, which reach HTTP calls but not terminal
    /// upgrades: those send only this password's credential. Any other value is sent exactly as written,
    /// as the pinned client sends it: a server configured
    /// with a whitespace password runs with that password. The SDK never reads credentials from
    /// the environment.
    /// </summary>
    public string? Password { get; set; }

    /// <summary>
    /// Gets or sets the ambient location, sent on every request as the
    /// <c>x-opencode-directory</c> header; <see langword="null"/> leaves the server's own
    /// resolution in place. The server honors the header only on the operations whose group
    /// resolves location from the request — operations that resolve it from a session instead,
    /// and those that do not resolve it at all, ignore it. A directory set for one call through
    /// <see cref="OpenCodeRequestOptions.Location"/> replaces this one on the same header for
    /// that call, and a generated request's own location member travels on the query channel,
    /// which the server reads first.
    /// </summary>
    public LocationSelector? Location { get; set; }

    /// <summary>
    /// Gets or sets how long an ordinary call may go without progress: waiting for the response
    /// headers, and then between reads of the body. The default, <see langword="null"/>, is
    /// 100 seconds. The operations that answer only when work ends — waiting for a session to go
    /// idle, running a shell command or a command, generating text, images or decisions, calling a
    /// plugin's method — ignore this value and run with no window, so the call's cancellation token
    /// is their deadline; a value set on
    /// <see cref="OpenCodeRequestOptions.NetworkTimeout"/> bounds them for that call. Accepts a
    /// value above zero and at most <see cref="int.MaxValue"/> milliseconds, or
    /// <see cref="Timeout.InfiniteTimeSpan"/> for no window.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative other than <see cref="Timeout.InfiniteTimeSpan"/>, or longer than <see cref="int.MaxValue"/> milliseconds.</exception>
    public TimeSpan? NetworkTimeout
    {
        get;
        set => field = Internal.TimerBounds.ValidateNetworkTimeout(value, nameof(value));
    }

    /// <summary>
    /// Gets or sets how long the live event stream (<c>Events.SubscribeAsync</c>) may receive
    /// nothing before it is cut and reconnected. The default, <see langword="null"/>, is 45
    /// seconds, as in upstream's own client. The server writes a keepalive every 15 seconds, so
    /// keep the window above that, or a quiet but healthy stream is cut and reconnected each time.
    /// The window does not run while your code holds an event. It is read once, when the client is
    /// built. Accepts a value above zero and at most <see cref="int.MaxValue"/> milliseconds; there
    /// is no window that never ends.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">
    /// The value is zero, negative, <see cref="Timeout.InfiniteTimeSpan"/>, or longer than
    /// <see cref="int.MaxValue"/> milliseconds.
    /// </exception>
    public TimeSpan? EventStreamIdleTimeout
    {
        get;
        set => field = Internal.TimerBounds.ValidateIdleWindow(value, nameof(value));
    }
}
