namespace OpenCode.Sdk.Internal;

/// <summary>The resolver's entry snapshot; managed validation happens only after the explicit branch.</summary>
internal sealed record ResolveRequest(
    Uri? Endpoint,
    string? Password,
    bool Standalone,
    bool Autostart,
    OpenCodeServerOptions? Launch,
    OpenCodeServerEnsureOptions? Service,
    Action<OpenCodeServerVersionMismatchContext>? OnServerVersionMismatch)
{
    public static ResolveRequest Snapshot(OpenCodeServerResolveOptions? options)
    {
        if (options?.Endpoint is not null && options.Standalone)
        {
            throw new ArgumentException("OpenCodeServerResolveOptions.Endpoint and Standalone cannot be combined.", nameof(options));
        }
        if (options?.Password is not null && options.Endpoint is null)
        {
            throw new ArgumentException(
                "Password applies only to an explicit Endpoint; a standalone or service connection carries its own credentials.", nameof(options));
        }
        if (options?.Endpoint is { } endpoint)
        {
            _ = EndpointPolicy.Normalize(endpoint);
        }

        return new ResolveRequest(options?.Endpoint, options?.Password, options?.Standalone ?? false,
            options?.Autostart ?? true, options?.Launch, options?.Service, options?.OnServerVersionMismatch);
    }
}
