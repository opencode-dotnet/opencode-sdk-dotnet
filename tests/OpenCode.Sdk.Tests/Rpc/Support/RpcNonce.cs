using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tests.Rpc.Support;

internal sealed record RpcNonce
{
    [JsonPropertyName("nonce")]
    public required string Nonce { get; init; }
}
