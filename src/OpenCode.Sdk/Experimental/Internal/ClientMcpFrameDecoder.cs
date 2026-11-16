using System.Net.WebSockets;
using System.Text.Json;

namespace OpenCode.Sdk.Internal;

/// <summary>Accepts object-valued text messages without inventing JSON-RPC validation errors.</summary>
internal sealed class ClientMcpFrameDecoder : ITerminalFrameDecoder<ClientMcpMessage>
{
    public ClientMcpMessage Decode(WebSocketMessageType messageType, byte[] payload, int count)
    {
        if (messageType is WebSocketMessageType.Text)
        {
            try
            {
                using var document = JsonDocument.Parse(new ReadOnlyMemory<byte>(payload, 0, count));
                if (document.RootElement.ValueKind is JsonValueKind.Object)
                {
                    return new ClientMcpMessage(document.RootElement.Clone());
                }
            }
            catch (JsonException)
            {
                return new ClientMcpMessage(default);
            }
        }

        return new ClientMcpMessage(default);
    }
}
