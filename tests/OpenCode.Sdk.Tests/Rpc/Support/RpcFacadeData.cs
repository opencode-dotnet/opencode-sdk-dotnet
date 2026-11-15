using System.Text.Json;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Rpc.Support;

internal static class RpcFacadeData
{
    public const string EchoName = "echo";
    public const string ObservedName = "observed";
    public const string ObservedType = "rpc.opencode.sdk.test.rpc.observed";
    public const string RejectedType = "sdk.test.rejected";
    public const string RejectedMessage = "SDK test RPC rejected the request.";
    public const string RejectedCode = "sdk-test-rejected";
    public const string Nonce = "typed-rpc-nonce";
    public const string EchoValue = "typed-rpc-input";
    public const string OutputNonce = "typed-rpc-output";
    public const string OutputValue = "typed-rpc-result";
    public const string Directory = "/repo";
    public const string EchoUri = "http://localhost:4096/api/rpc/opencode.sdk.test.rpc/echo?location[directory]=%2Frepo";
    public const string EchoInputValue = "{\"nonce\":\"typed-rpc-nonce\",\"value\":\"typed-rpc-input\"}";
    public const string EchoInputBody = "{\"input\":" + EchoInputValue + "}";
    public const string EchoWireInputBody = "{\"input\":\"typed-rpc-nonce|typed-rpc-input\"}";
    public const string EchoOutputValue = "{\"nonce\":\"typed-rpc-output\",\"value\":\"typed-rpc-result\"}";
    public const string EchoOutputBody = "{\"output\":" + EchoOutputValue + "}";
    public const string JsonNull = "null";
    public const string NullInputBody = "{\"input\":null}";
    public const string AbsentOutputBody = "{}";
    public const string NullOutputBody = "{\"output\":null}";
    public const string UnreadableOutputBody = "{\"output\":{\"nonce\":7,\"value\":\"v\"}}";
    public const string OutputFailureMessage = "The output of RPC method 'opencode.sdk.test.rpc.echo' does not match its output type.";
    public const string InternalType = "rpc.internal";
    public const string InvalidOutputType = "rpc.invalid_output";
    public const string UnavailableType = "rpc.unavailable";
    public const string InternalMessage = "RPC call failed";
    public const string UnavailableMessage = "RPC is unavailable: opencode.sdk.test.rpc";
    public const string InvalidOutputMessage = "Invalid RPC output.";
    public const string UnparsedErrorBody = "not-json";
    public const string RpcErrorTag = "RpcError";
    public const string DeclaredErrorMethod = "declaredError";
    public const string InternalErrorMethod = "internalError";
    public const string InvalidOutputMethod = "invalidOutput";
    public const string RejectionData = "{\"code\":\"sdk-test-rejected\",\"nonce\":\"typed-rpc-nonce\"}";
    public const string TaggedRejectionBody =
        "{\"_tag\":\"RpcError\",\"type\":\"sdk.test.rejected\",\"message\":\"SDK test RPC rejected the request.\"}";
    public const string RejectionBody =
        "{\"_tag\":\"RpcError\",\"type\":\"" + RejectedType + "\",\"message\":\"" + RejectedMessage + "\",\"data\":" + RejectionData + "}";
    public const string InternalErrorBody =
        "{\"_tag\":\"RpcInternalError\",\"type\":\"" + InvalidOutputType + "\",\"message\":\"" + InvalidOutputMessage + "\"}";
    public const string InternalErrorDataBody =
        "{\"_tag\":\"RpcInternalError\",\"type\":\"" + InternalType + "\",\"message\":\"" + InternalMessage + "\",\"data\":" + RejectionData + "}";
    public const string UnavailableBody =
        "{\"_tag\":\"RpcError\",\"type\":\"" + UnavailableType + "\",\"message\":\"" + UnavailableMessage + "\"}";
    public static readonly RpcDefinition Definition = new(TestRpcPlugin.Id);
    public static readonly RpcMethod<RpcEcho, RpcEcho> Echo =
        Definition.Method(EchoName, RpcFacadeTestJson.Default.RpcEcho, RpcFacadeTestJson.Default.RpcEcho);
    public static readonly RpcMethod<JsonElement?, RpcEcho> RawEcho =
        Definition.Method(EchoName, RpcFacadeTestJson.Default.NullableJsonElement, RpcFacadeTestJson.Default.RpcEcho);
    public static readonly RpcMethod<RpcEcho, int> Count =
        Definition.Method(EchoName, RpcFacadeTestJson.Default.RpcEcho, RpcFacadeTestJson.Default.Int32);
    public static readonly RpcMethod<RpcNonce, RpcNonce> Emit =
        Definition.Method("emit", RpcFacadeTestJson.Default.RpcNonce, RpcFacadeTestJson.Default.RpcNonce);
    public static readonly RpcMethod<RpcEcho, RpcEcho> WireEcho = Definition.Method(EchoName,
        new RpcFacadeTestJson(new JsonSerializerOptions { Converters = { new RpcEchoWireConverter() } }).RpcEcho,
        RpcFacadeTestJson.Default.RpcEcho);
    public static readonly RpcMethod<RpcEcho, RpcEcho> FailingEcho = Definition.Method(EchoName, RpcFacadeTestJson.Default.RpcEcho,
        new RpcFacadeTestJson(new JsonSerializerOptions { Converters = { new RpcFailingEchoConverter() } }).RpcEcho);
    public static readonly InvalidOperationException MetadataFailure = new("RPC metadata sentinel.");
    public static readonly RpcEcho EchoInput = new() { Nonce = Nonce, Value = EchoValue };

    public static RpcMethod<RpcNonce, RpcEcho> NonceToEcho(string method) =>
        Definition.Method(method, RpcFacadeTestJson.Default.RpcNonce, RpcFacadeTestJson.Default.RpcEcho);

    public static JsonElement Element(string json)
    {
        using var document = JsonDocument.Parse(json);
        return document.RootElement.Clone();
    }
}
