using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tests.Rpc.Support;

internal sealed class RpcFailingNonceConverter : JsonConverter<RpcNonce>
{
    public override RpcNonce Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw RpcFacadeData.MetadataFailure;

    public override void Write(Utf8JsonWriter writer, RpcNonce value, JsonSerializerOptions options) => throw new NotSupportedException();
}
