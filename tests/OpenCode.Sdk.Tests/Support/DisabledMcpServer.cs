using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Support;

/// <summary>A runtime MCP entry live tests own: registered disabled with OAuth off at an inert <c>.invalid</c> URL, so the
/// server never connects, and removed tolerating only the exact already-missing refusal.</summary>
internal static class DisabledMcpServer
{
    public static async Task AddAsync(OpenCodeClient client, string name, CancellationToken token) =>
        _ = await client.Experimental.AddMcpServerAsync(name, new ExperimentalMcpAddRequest
        {
            Config = new McpRemoteConfig
            {
                Disabled = true,
                OAuth = McpRemoteConfigOAuth.FromBoolean(false),
                Url = "https://sdk-coverage.invalid/mcp",
            },
        }, cancellationToken: token);

    public static async Task RemoveOwnedAsync(OpenCodeClient client, string name, CancellationToken token)
    {
        var response = await client.Experimental.RemoveMcpServerAsync(name, requestOptions: OpenCodeRequestOptions.NoThrow, cancellationToken: token);
        if (response.Status == 204 || (response is { Status: 404, Error: McpServerNotFoundError missing } && missing.Server == name))
        {
            return;
        }

        throw new InvalidOperationException("Owned MCP removal failed: " + response.Error?.GetType().Name);
    }
}
