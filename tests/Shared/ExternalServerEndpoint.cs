namespace OpenCode.Sdk.Tests.Harness;

/// <summary>
/// The exact-pin fixture's explicit-endpoint pair: an
/// operator-supplied server the fixture attaches to instead of spawning one, resolved once from
/// <c>OPENCODE_SDK_TESTS_ENDPOINT</c>/<c>OPENCODE_SDK_TESTS_PASSWORD</c>.
/// </summary>
internal sealed record ExternalServerEndpoint(Uri Endpoint, string Password)
{
    /// <summary>
    /// Resolves the pair from the real process environment through
    /// <see cref="Environment.GetEnvironmentVariable(string)"/>.
    /// </summary>
    public static ExternalServerEndpoint? FromEnvironment() => FromEnvironment(Environment.GetEnvironmentVariable);

    /// <summary>
    /// Test seam: resolves the pair through an injected reader instead of the real process
    /// environment, so the resolution rules are testable without mutating ambient state.
    /// </summary>
    /// <param name="read">
    /// Reads one named environment variable; mirrors
    /// <see cref="Environment.GetEnvironmentVariable(string)"/> (<see langword="null"/> for
    /// unset).
    /// </param>
    /// <returns>
    /// The pair when both variables are set; <see langword="null"/> when neither is set.
    /// </returns>
    /// <exception cref="InvalidOperationException">
    /// Exactly one of the two variables is set, <c>OPENCODE_SDK_TESTS_PASSWORD</c> is blank or
    /// whitespace, or <c>OPENCODE_SDK_TESTS_ENDPOINT</c> does not parse as an absolute
    /// <c>http</c>/<c>https</c> URI.
    /// </exception>
    public static ExternalServerEndpoint? FromEnvironment(Func<string, string?> read)
    {
        ArgumentNullException.ThrowIfNull(read);

        var endpoint = read("OPENCODE_SDK_TESTS_ENDPOINT");
        var password = read("OPENCODE_SDK_TESTS_PASSWORD");
        if (endpoint is null && password is null)
        {
            return null;
        }

        if (endpoint is null || password is null)
        {
            throw new InvalidOperationException(
                "OPENCODE_SDK_TESTS_ENDPOINT and OPENCODE_SDK_TESTS_PASSWORD must both be set, or neither " +
                "(the exact-pin fixture's external-endpoint mode needs both to attach to an operator-supplied " +
                "server).");
        }

        if (!Uri.TryCreate(endpoint, UriKind.Absolute, out var parsed) ||
            (parsed.Scheme != Uri.UriSchemeHttp && parsed.Scheme != Uri.UriSchemeHttps))
        {
            throw new InvalidOperationException(
                $"OPENCODE_SDK_TESTS_ENDPOINT ('{endpoint}') must be an absolute http or https URI.");
        }

        return new ExternalServerEndpoint(parsed, password);
    }

    /// <summary>
    /// Renders the pair by its endpoint alone. The positional record's synthesized rendering
    /// prints every member, so an attach banner, an assertion message, or a failing test's
    /// diagnostic would carry the operator's password verbatim; the endpoint is the only half
    /// that identifies the server.
    /// </summary>
    /// <returns>The record's name and its endpoint, never the password.</returns>
    public override string ToString() => nameof(ExternalServerEndpoint) + " { Endpoint = " + Endpoint + " }";
}
