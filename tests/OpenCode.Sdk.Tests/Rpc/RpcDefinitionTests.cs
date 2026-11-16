using System.Text.Json;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Rpc.Support;

namespace OpenCode.Sdk.Tests.Rpc;

public sealed class RpcDefinitionTests
{
    [Test]
    public async Task RpcDefinition_Should_Preserve_Descriptor_Identity_And_Metadata()
    {
        var definition = new RpcDefinition(TestRpcPlugin.Id);
        var input = RpcFacadeTestJson.Default.RpcEcho;
        var output = new RpcFacadeTestJson(new JsonSerializerOptions()).RpcEcho;
        var method = definition.Method(RpcFacadeData.EchoName, input, output);
        var data = RpcFacadeTestJson.Default.RpcNonce;
        var rpcEvent = definition.Event(RpcFacadeData.ObservedName, data);

        await Assert.That(definition.Id).IsEqualTo(TestRpcPlugin.Id);
        await Assert.That(method.Definition).IsSameReferenceAs(definition);
        await Assert.That(method.Name).IsEqualTo(RpcFacadeData.EchoName);
        await Assert.That(method.Input).IsSameReferenceAs(input);
        await Assert.That(method.Output).IsSameReferenceAs(output);
        await Assert.That(rpcEvent.Definition).IsSameReferenceAs(definition);
        await Assert.That(rpcEvent.Name).IsEqualTo(RpcFacadeData.ObservedName);
        await Assert.That(rpcEvent.Data).IsSameReferenceAs(data);
        await Assert.That(rpcEvent.Type).IsEqualTo(RpcFacadeData.ObservedType);
    }

    [Test]
    [Arguments("constructor", null, false, false, nameof(ArgumentNullException), "id")]
    [Arguments("constructor", "", false, false, nameof(ArgumentException), "id")]
    [Arguments("constructor", "  ", false, false, nameof(ArgumentException), "id")]
    [Arguments("method", null, false, false, nameof(ArgumentNullException), "name")]
    [Arguments("method", "", false, false, nameof(ArgumentException), "name")]
    [Arguments("method", "  ", false, false, nameof(ArgumentException), "name")]
    [Arguments("method", RpcFacadeData.EchoName, true, false, nameof(ArgumentNullException), "input")]
    [Arguments("method", RpcFacadeData.EchoName, false, true, nameof(ArgumentNullException), "output")]
    [Arguments("event", null, false, false, nameof(ArgumentNullException), "name")]
    [Arguments("event", "", false, false, nameof(ArgumentException), "name")]
    [Arguments("event", "  ", false, false, nameof(ArgumentException), "name")]
    [Arguments("event", RpcFacadeData.ObservedName, true, false, nameof(ArgumentNullException), "data")]
    [Arguments("method", null, true, true, nameof(ArgumentNullException), "name")]
    [Arguments("method", RpcFacadeData.EchoName, true, true, nameof(ArgumentNullException), "input")]
    [Arguments("event", null, true, false, nameof(ArgumentNullException), "name")]
    public async Task RpcDefinition_Should_Refuse_Invalid_Arguments_In_Order(
        string member, string? name, bool nullInput, bool nullOutput, string exceptionType, string parameter)
    {
        // Null-forgiving operators deliberately supply invalid arguments to the public guards.
        var input = nullInput ? null! : RpcFacadeTestJson.Default.RpcEcho;
        var output = nullOutput ? null! : RpcFacadeTestJson.Default.RpcEcho;
        Action call = member switch
        {
            "constructor" => () => _ = new RpcDefinition(name!),
            "method" => () => _ = RpcFacadeData.Definition.Method(name!, input, output),
            "event" => () => _ = RpcFacadeData.Definition.Event(name!, input),
            _ => throw new InvalidOperationException(member),
        };

        var exception = exceptionType == nameof(ArgumentNullException)
            ? await Assert.That(call).ThrowsExactly<ArgumentNullException>()
            : await Assert.That(call).ThrowsExactly<ArgumentException>();

        await Assert.That(exception?.ParamName).IsEqualTo(parameter);
    }
}
