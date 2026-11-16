using System.Diagnostics.CodeAnalysis;
using OpenCode.Sdk.Internal;

namespace OpenCode.Sdk;

/// <summary>Exposes application-owned tools to OpenCode as an experimental MCP server.</summary>
[Experimental("OPENCODE001", UrlFormat = "https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md#opencode001")]
public static class ExperimentalClientMcpExtensions
{
    /// <summary>Begins a connection immediately; initial factory and setup failures are synchronous.</summary>
    /// <param name="client">The experimental client providing the root endpoint, credential and clock.</param>
    /// <param name="options">The namespace, ordered tools and optional base URL and header overrides.</param>
    /// <returns>A caller-owned reconnecting handle, not a readiness or registration acknowledgment.</returns>
    /// <remarks>An explicit URL receives the root credential unless its handshake headers override Authorization.</remarks>
    public static ClientMcpConnection ConnectClientMcp(this ExperimentalClient client, ClientMcpOptions options)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(options);
        if (options.Name is null || options.Tools is null || options.Tools.Any(static tool => tool is null || tool.Name is null || tool.Handler is null))
        {
            throw new ArgumentException("Client MCP name, tools and handlers are required.", nameof(options));
        }

        var pipeline = client.PipelineOrNull ?? throw MockSeam.CreateError(nameof(ExperimentalClient), nameof(ConnectClientMcp));
        var connector = new ClientMcpConnector(pipeline.Connection, options);
        return new ClientMcpConnection(options, pipeline.Connection.Time, connector.Create,
            (socket, token) => connector.UpgradeAsync((ClientTerminalWebSocket)socket, token));
    }
}
