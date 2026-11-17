namespace OpenCode.Sdk.Internal;

/// <summary>Builds clients with door-owned identity and caller-configured behavior.</summary>
internal static class ClientIdentityComposer
{
    public static OpenCodeClient CreateClient(Uri endpoint, string? password, Action<OpenCodeClientOptions>? configure)
    {
        var options = new OpenCodeClientOptions();
        configure?.Invoke(options);
        if (options.Endpoint is not null ||
            options.Password is not null ||
            !string.Equals(options.Username, "opencode", StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "CreateClient owns the connection identity: Endpoint and Password must stay null and Username must stay \"opencode\"; the server supplies them. Every other option is carried into the client.");
        }

        // A distinct instance carries the door's identity into the client: the object the
        // delegate received stays identity-unset for as long as the caller keeps a reference to
        // it, rather than being mutated out from under them after the delegate returns.
        var clientOptions = new OpenCodeClientOptions
        {
            Endpoint = endpoint,
            Username = options.Username,
            Password = password,
            Location = options.Location,
            HttpHandler = options.HttpHandler,
            NetworkTimeout = options.NetworkTimeout,
            EventStreamIdleTimeout = options.EventStreamIdleTimeout,
            MediaReferences = options.MediaReferences,
        };
        foreach (var header in options.Headers)
        {
            clientOptions.Headers.Add(header);
        }

        return new OpenCodeClient(clientOptions);
    }
}
