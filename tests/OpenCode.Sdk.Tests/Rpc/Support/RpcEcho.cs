using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tests.Rpc.Support;

internal sealed record RpcEcho
{
    [JsonPropertyName("nonce")]
    public required string Nonce { get; init; }

    [JsonPropertyName("value")]
    public required string Value { get; init; }
}
