namespace OpenCode.Sdk;

/// <summary>Configures one opencode API call.</summary>
public sealed class OpenCodeRequestOptions
{
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

    /// <summary>Gets a shared instance that returns API errors on the response envelope.</summary>
    public static OpenCodeRequestOptions NoThrow { get; } = new()
    {
        ErrorBehavior = ErrorBehavior.NoThrow,
    };
}
