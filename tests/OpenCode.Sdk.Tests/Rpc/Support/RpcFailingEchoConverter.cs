using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tests.Rpc.Support;

internal sealed class RpcFailingEchoConverter : JsonConverter<RpcEcho>
{
    public override RpcEcho Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options) =>
        throw RpcFacadeData.MetadataFailure;

    public override void Write(Utf8JsonWriter writer, RpcEcho value, JsonSerializerOptions options) => throw new NotSupportedException();
}
