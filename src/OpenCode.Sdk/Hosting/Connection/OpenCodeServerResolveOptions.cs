namespace OpenCode.Sdk;

/// <summary>Selects an explicit server, a private standalone server, or the shared managed service.</summary>
public sealed class OpenCodeServerResolveOptions
{
    /// <summary>Gets or sets the explicit endpoint; managed options are ignored when set.</summary>
    public Uri? Endpoint { get; set; }

    /// <summary>Gets or sets the explicit endpoint's password; null sends no credential, while an empty string sends one.</summary>
    /// <remarks>A password without an Endpoint is refused; no credential environment variable is read.</remarks>
    public string? Password { get; set; }

    /// <summary>Gets or sets a value indicating whether to start a private standalone server without reading service config.</summary>
    /// <remarks>Cannot be combined with Endpoint.</remarks>
    public bool Standalone { get; set; }

    /// <summary>Gets or sets a value indicating whether to ensure the managed service; false discovers any version and starts standalone on a miss.</summary>
    public bool Autostart { get; set; } = true;

    /// <summary>Gets or sets launch options for every standalone path, ignored for an explicit endpoint or a service hit.</summary>
    public OpenCodeServerOptions? Launch { get; set; }

    /// <summary>Gets or sets managed selection, version policy, command, environment and initial start callback.</summary>
    /// <remarks>Ignored and not validated for an explicit Endpoint. Replace and Error require ExpectedVersion; the pinned release is never a default.</remarks>
    public OpenCodeServerEnsureOptions? Service { get; set; }

    /// <summary>Gets or sets the synchronous explicit-version mismatch callback, silent by default.</summary>
    /// <remarks>Invoked once on an ordinal mismatch against the pinned release; resolution continues unless the callback throws.</remarks>
    public Action<OpenCodeServerVersionMismatchContext>? OnServerVersionMismatch { get; set; }
}
