using System.Net.WebSockets;
using System.Text;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Experimental.Support;

internal sealed class ClientMcpLiveWire(SimulatedDriveServerFixture server, LocationSelector location) : IDisposable
{
    public ClientWebSocket Socket { get; } = new();

    public Task ConnectAsync(CancellationToken cancellationToken)
    {
        var authorization = Convert.ToBase64String(Encoding.UTF8.GetBytes("opencode:" + server.Server.Password));
        Socket.Options.SetRequestHeader("Authorization", "Basic " + authorization);
        return ConnectAsync("live", null, cancellationToken);
    }

    public async Task ConnectAsync(string name, string? ticket, CancellationToken cancellationToken)
    {
#if NET
        Socket.Options.CollectHttpResponseDetails = true;
#endif
        var endpoint = new UriBuilder(new Uri(server.Endpoint, "api/experimental/client-mcp"))
        {
            Scheme = "ws",
            Query = "name=" + Uri.EscapeDataString(name) + "&location%5Bdirectory%5D=" + Uri.EscapeDataString(location.Directory!)
                + (ticket is null ? "" : "&ticket=" + Uri.EscapeDataString(ticket)),
        };
        await Socket.ConnectAsync(endpoint.Uri, cancellationToken);
    }

    public async Task<JsonObject?> ReadAsync(CancellationToken cancellationToken)
    {
        var bytes = new List<byte>();
        WebSocketReceiveResult frame;
        do
        {
            var buffer = new byte[4096];
            frame = await Socket.ReceiveAsync(new ArraySegment<byte>(buffer), cancellationToken);
            if (frame.MessageType is WebSocketMessageType.Close)
            {
                return null;
            }

            bytes.AddRange(buffer.Take(frame.Count));
        } while (!frame.EndOfMessage);

        return JsonNode.Parse(Encoding.UTF8.GetString([.. bytes]))!.AsObject();
    }

    public Task ReplyAsync(JsonObject request, JsonObject response, CancellationToken cancellationToken)
    {
        response["jsonrpc"] = "2.0";
        response["id"] = request["id"]!.DeepClone();
        var bytes = Encoding.UTF8.GetBytes(response.ToJsonString());
        return Socket.SendAsync(new ArraySegment<byte>(bytes), WebSocketMessageType.Text, endOfMessage: true, cancellationToken);
    }

    public void Dispose() => Socket.Dispose();
}
