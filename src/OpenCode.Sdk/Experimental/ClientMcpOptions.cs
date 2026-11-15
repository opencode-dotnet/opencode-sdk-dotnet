using System.Diagnostics.CodeAnalysis;
using System.Net.WebSockets;

namespace OpenCode.Sdk;

/// <summary>Describes one caller-owned, reconnecting client-hosted MCP connection.</summary>
/// <remarks>Collections are shallow references; do not mutate them while connected.</remarks>
[Experimental("OPENCODE001", UrlFormat = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode001")]
public sealed record ClientMcpOptions
{
    /// <summary>Gets the MCP namespace; the helper adds no name validator.</summary>
    public required string Name { get; init; }

    /// <summary>Gets tools in declaration order; an empty list is valid and the first duplicate wins.</summary>
    public required IReadOnlyList<ClientMcpTool> Tools { get; init; }

    /// <summary>Gets the base URL override, retaining its path and query; null uses the root endpoint.</summary>
    /// <remarks>The chosen destination receives the root credential unless Headers overrides Authorization.</remarks>
    public Uri? Url { get; init; }

    /// <summary>Gets the explicit directory; ambient location applies only when Url is unset.</summary>
    public LocationSelector? Location { get; init; }

    /// <summary>Gets handshake headers, overriding the root Authorization default.</summary>
    public IReadOnlyDictionary<string, string>? Headers { get; init; }

    /// <summary>Gets a factory for fresh unconnected sockets; every returned socket becomes SDK-owned.</summary>
    public Func<ClientWebSocket>? WebSocketFactory { get; init; }

    /// <summary>Prints only the name, never URL or header credentials.</summary>
    /// <returns>The safe connection description.</returns>
    public override string ToString() => $"ClientMcpOptions {{ Name = {Name} }}";
}
