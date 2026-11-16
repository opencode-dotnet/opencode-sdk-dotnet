using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tests.Rpc.Support;

internal sealed class RpcEchoWireConverter : JsonConverter<RpcEcho>
{
    public override RpcEcho Read(ref Utf8JsonReader reader, Type typeToConvert, JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("The echo wire value must be a string.");
        }
        var text = reader.GetString() ?? throw new JsonException("The echo wire value was null.");
        var separator = text.IndexOf('|', StringComparison.Ordinal);
        if (separator < 0)
        {
            throw new JsonException("The echo wire value has no separator.");
        }
        return new RpcEcho { Nonce = text[..separator], Value = text[(separator + 1)..] };
    }

    public override void Write(Utf8JsonWriter writer, RpcEcho value, JsonSerializerOptions options) =>
        writer.WriteStringValue(value.Nonce + "|" + value.Value);
}
