namespace OpenCode.Sdk.Internal;

/// <summary>Validates a server endpoint and normalizes it into the request base.</summary>
internal static class EndpointPolicy
{
    /// <summary>Validates the endpoint and returns the base every route is appended to.</summary>
    /// <param name="endpoint">The configured server endpoint.</param>
    /// <returns>The normalized base: scheme, authority, and path prefix without a trailing slash.</returns>
    public static string Normalize(Uri endpoint)
    {
        ArgumentNullException.ThrowIfNull(endpoint);

        if (!endpoint.IsAbsoluteUri)
        {
            throw new ArgumentException("The endpoint must be an absolute URI.", nameof(endpoint));
        }

        if (endpoint.Scheme is not ("http" or "https"))
        {
            throw new ArgumentException("The endpoint scheme must be HTTP or HTTPS.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpoint.Query))
        {
            throw new ArgumentException("The endpoint must not carry a query.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpoint.Fragment))
        {
            throw new ArgumentException("The endpoint must not carry a fragment.", nameof(endpoint));
        }

        if (!string.IsNullOrEmpty(endpoint.UserInfo))
        {
            throw new ArgumentException("The endpoint must not carry user information.", nameof(endpoint));
        }

        return $"{endpoint.Scheme}://{endpoint.Authority}{endpoint.AbsolutePath.TrimEnd('/')}";
    }
}
