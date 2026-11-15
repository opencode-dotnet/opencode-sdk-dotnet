namespace OpenCode.Sdk.Internal;

/// <summary>Builds the client-MCP upgrade's base-path and query-preserving address.</summary>
internal static class ClientMcpConnectUriBuilder
{
    public static Uri Build(ConnectionSnapshot connection, ClientMcpOptions options)
    {
        var address = options.Url ?? new Uri(connection.EndpointBase);
        if (!address.IsAbsoluteUri)
        {
            throw new ArgumentException("The client MCP base URL must be absolute.", nameof(options));
        }

        var builder = new UriBuilder(address)
        {
            Scheme = address.Scheme is "https" or "wss" ? "wss" : "ws",
            Path = address.AbsolutePath + (address.AbsolutePath.EndsWith('/', StringComparison.Ordinal) ? string.Empty : "/") + "api/experimental/client-mcp",
            Fragment = string.Empty,
        };
        var query = address.Query.TrimStart('?');
        var directory = options.Url is null
            ? LocationMerge.Resolve(options.Location, connection.Location)?.Directory
            : options.Location?.Directory;
        if (directory is { Length: > 0 })
        {
            query = Set(query, "location[directory]", directory);
        }

        builder.Query = Set(query, "name", options.Name);
        return builder.Uri;
    }

    private static string Set(string query, string name, string value)
    {
        var replacement = Uri.EscapeDataString(name) + "=" + Uri.EscapeDataString(value);
        var parts = new List<string>();
        var replaced = false;
        foreach (var part in query.Length is 0 ? [] : query.Split('&'))
        {
            var key = part.Split('=')[0];
            if (!string.Equals(Uri.UnescapeDataString(key.Replace('+', ' ')), name, StringComparison.Ordinal))
            {
                parts.Add(part);
                continue;
            }

            if (!replaced)
            {
                parts.Add(replacement);
                replaced = true;
            }
        }

        if (!replaced)
        {
            parts.Add(replacement);
        }

        return string.Join('&', parts);
    }
}
