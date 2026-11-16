using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tests.Rpc.Support;

internal sealed record RpcRejection
{
    [JsonPropertyName("code")]
    public required string Code { get; init; }

    [JsonPropertyName("nonce")]
    public required string Nonce { get; init; }
}
