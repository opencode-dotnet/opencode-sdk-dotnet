using System.Text.Json;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Internal;

/// <summary>Invokes a definition's method through the virtual RPC door and reads its typed output.</summary>
internal sealed class RpcMethodCaller
{
    private readonly RpcClient _client;

    public RpcMethodCaller(RpcClient client)
    {
        _client = client;
    }

    public async Task<TOutput?> CallAsync<TInput, TOutput>(RpcMethod<TInput, TOutput> method, TInput input,
        LocationSelector? location, OpenCodeRequestOptions? requestOptions, CancellationToken cancellationToken)
    {
        var request = new RpcCallRequest { Input = JsonSerializer.SerializeToElement(input, method.Input), Location = location };
        RpcCallResponse response;
        try
        {
            response = await _client.CallAsync(method.Definition.Id, method.Name, request, requestOptions, cancellationToken).ConfigureAwait(false);
        }
        catch (OpenCodeApiException exception) when (exception.Error is RpcError or RpcInternalError)
        {
            if (exception.Error is RpcError error)
            {
                throw new OpenCodeRpcException(error.Type, error.Message, error.Data, exception.Status, exception.Error, exception.RawBody);
            }

            var internalError = (RpcInternalError)exception.Error;
            throw new OpenCodeRpcException(internalError.Type.Value, internalError.Message, internalError.Data,
                exception.Status, exception.Error, exception.RawBody);
        }

        try
        {
            return response.Call.Output is { } element
                ? JsonSerializer.Deserialize(element, method.Output)
                : JsonSerializer.Deserialize("null"u8, method.Output);
        }
        catch (JsonException exception)
        {
            throw new OpenCodeTransportException(
                "The output of RPC method '" + method.Definition.Id + "." + method.Name + "' does not match its output type.", exception);
        }
    }
}
