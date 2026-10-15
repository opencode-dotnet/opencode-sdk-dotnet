namespace OpenCode.Sdk;

/// <summary>
/// Configures an opencode client for its whole lifetime. The class stays settable for the
/// options and configuration-binding patterns; a client snapshots it at construction, so later
/// mutation never reaches a built client.
/// </summary>
public sealed class OpenCodeClientOptions
{
    /// <summary>Gets or sets the absolute HTTP or HTTPS server endpoint; required to build a client.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>
    /// Gets or sets the HTTP basic-authentication username; the default is
    /// <c>opencode</c>, the only username the pinned server accepts.
    /// </summary>
    public string Username { get; set; } = "opencode";

    /// <summary>
    /// Gets or sets the HTTP basic-authentication password. Every server the <c>opencode</c>
    /// CLI starts requires one — the value set through <c>OPENCODE_PASSWORD</c>, or the one the
    /// CLI generates and prints as <c>server password &lt;pw&gt;</c> when none is configured — so
    /// <see langword="null"/>, which sends no credential at all, is right only for a host that runs
    /// without authentication, such as a server embedded through the opencode server library. Any
    /// other value is sent exactly as written, as the pinned client sends it: a server configured
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
    /// idle, running a shell command or a command, generating text — ignore this value and run with
    /// no window,
    /// so the call's cancellation token is their deadline; a value set on
    /// <see cref="OpenCodeRequestOptions.NetworkTimeout"/> bounds them for that call. Accepts a
    /// value above zero and at most <see cref="int.MaxValue"/> milliseconds, or
    /// <see cref="Timeout.InfiniteTimeSpan"/> for no window.
    /// </summary>
    /// <exception cref="ArgumentOutOfRangeException">The value is zero, negative other than <see cref="Timeout.InfiniteTimeSpan"/>, or longer than <see cref="int.MaxValue"/> milliseconds.</exception>
    public TimeSpan? NetworkTimeout
    {
        get;
        set => field = Internal.NetworkTimeouts.Validate(value, nameof(value));
    }
}
