using System.Runtime.CompilerServices;
using System.Text.Json;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

internal sealed class RpcEventReader(EventsClient client)
{
    public async IAsyncEnumerable<RpcEventPayload<TData>> ReadAsync<TData>(
        RpcEventDefinition<TData> rpcEvent, [EnumeratorCancellation] CancellationToken cancellationToken)
    {
        await foreach (var item in client.SubscribeAsync(null, null, cancellationToken).ConfigureAwait(false))
        {
            if (item is not EventRpc rpc || !string.Equals(rpc.Type, rpcEvent.Type, StringComparison.Ordinal))
            {
                continue;
            }

            var bytes = JsonSerializer.SerializeToUtf8Bytes(rpc.Data, OpenCodeJsonContext.Default.IReadOnlyDictionaryStringJsonElement);
            TData data;
            try
            {
                data = JsonSerializer.Deserialize(bytes, rpcEvent.Data) ?? throw new JsonException("The event data read as null.");
            }
            catch (JsonException exception)
            {
                throw new OpenCodeTransportException("The data of RPC event '" + rpcEvent.Type + "' does not match its data type.", exception);
            }

            yield return new RpcEventPayload<TData> { Event = rpc, Data = data };
        }
    }
}
