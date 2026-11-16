using System.Net.WebSockets;

namespace OpenCode.Sdk.Internal;

/// <summary>Owns synchronous construction and asynchronous upgrade until a socket transfers to its core.</summary>
internal sealed class ClientMcpConnector(ConnectionSnapshot connection, ClientMcpOptions options, Action<ClientWebSocket>? disposed = null)
{
    private readonly Uri _uri = ClientMcpConnectUriBuilder.Build(connection, options);

    public ClientTerminalWebSocket Create()
    {
        try
        {
            return options.WebSocketFactory is { } factory
                ? new ClientTerminalWebSocket(factory(), connection.Authorization, options.Headers, disposed)
                : new ClientTerminalWebSocket(connection.Authorization, options.Headers, disposed, connection.HttpHandler);
        }
        catch (PlatformNotSupportedException exception) when (options.WebSocketFactory is null)
        {
            throw new OpenCodeTransportException($"The opencode client MCP '{options.Name}' WebSocket could not be constructed on this platform.", exception);
        }
    }

    public async Task<ITerminalWebSocket> UpgradeAsync(ClientTerminalWebSocket socket, CancellationToken token)
    {
        try
        {
            await socket.ConnectAsync(_uri, options.Name, new ClientMcpUpgradeFailurePolicy(), token).ConfigureAwait(false);
            return socket;
        }
        catch
        {
            socket.Dispose();
            throw;
        }
    }

}
