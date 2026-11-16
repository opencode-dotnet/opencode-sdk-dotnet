using System.Text.Json;
using System.Text.Json.Serialization;

namespace OpenCode.Sdk.Tests.Rpc.Support;

[JsonSourceGenerationOptions(GenerationMode = JsonSourceGenerationMode.Metadata)]
[JsonSerializable(typeof(RpcEcho))]
[JsonSerializable(typeof(RpcNonce))]
[JsonSerializable(typeof(RpcRejection))]
[JsonSerializable(typeof(JsonElement?))]
[JsonSerializable(typeof(int))]
internal sealed partial class RpcFacadeTestJson : JsonSerializerContext;
