using System.Text.Json;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Rpc.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Rpc;

[ClassDataSource<PinnedOpenCodeServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel]
public sealed class RpcFacadeLiveTests(PinnedOpenCodeServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task CallAsync_Should_Return_The_Typed_Echo_From_The_Owned_Plugin(CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        if (await AssertExternalUnavailableAsync(client, RpcFacadeData.EchoName, cancellationToken))
        {
            return;
        }
        await LiveReadiness.PluginAsync(client, TestRpcPlugin.Id, cancellationToken);
        var input = new RpcEcho { Nonce = Guid.NewGuid().ToString("N"), Value = RpcFacadeData.EchoValue };

        var output = await client.Rpc.CallAsync(RpcFacadeData.Echo, input, cancellationToken: cancellationToken);

        await Assert.That(output?.Nonce).IsEqualTo(input.Nonce);
        await Assert.That(output?.Value).IsEqualTo(input.Value);
        Console.WriteLine("rpc-facade-live: mode=owned arm=echo nonce=" + input.Nonce);
    }

    [Test]
    [Timeout(60_000)]
    [Arguments(RpcFacadeData.DeclaredErrorMethod, RpcFacadeData.RejectedType, 400, RpcFacadeData.RejectedMessage)]
    [Arguments(RpcFacadeData.InternalErrorMethod, RpcFacadeData.InternalType, 500, RpcFacadeData.InternalMessage)]
    [Arguments(RpcFacadeData.InvalidOutputMethod, RpcFacadeData.InvalidOutputType, 500, null)]
    public async Task CallAsync_Should_Throw_The_Plugin_Failure_Typed(
        string method, string errorType, int status, string? message, CancellationToken cancellationToken)
    {
        using var client = server.CreateClient();
        if (await AssertExternalUnavailableAsync(client, method, cancellationToken))
        {
            return;
        }
        await LiveReadiness.PluginAsync(client, TestRpcPlugin.Id, cancellationToken);
        var input = new RpcNonce { Nonce = Guid.NewGuid().ToString("N") };

        var failure = await Assert.That(() => client.Rpc.CallAsync(RpcFacadeData.NonceToEcho(method), input,
            cancellationToken: cancellationToken)).ThrowsExactly<OpenCodeRpcException>()
            ?? throw new InvalidOperationException("The RPC failure was absent.");

        await Assert.That(failure).IsAssignableTo<OpenCodeApiException>();
        await Assert.That(failure.ErrorType).IsEqualTo(errorType);
        await Assert.That(failure.Status).IsEqualTo(status);
        if (message is null)
        {
            await Assert.That(failure.Message).IsNotEmpty();
        }
        else
        {
            await Assert.That(failure.Message).IsEqualTo(message);
        }
        if (method == RpcFacadeData.DeclaredErrorMethod)
        {
            var data = failure.ErrorData ?? throw new InvalidOperationException("The declared error data was absent.");
            var rejection = JsonSerializer.Deserialize(data, RpcFacadeTestJson.Default.RpcRejection);
            await Assert.That(rejection?.Code).IsEqualTo(RpcFacadeData.RejectedCode);
            await Assert.That(rejection?.Nonce).IsEqualTo(input.Nonce);
        }
        else
        {
            await Assert.That(failure.ErrorData).IsNull();
        }
        Console.WriteLine("rpc-facade-live: mode=owned arm=" + method + " type=" + failure.ErrorType);
    }

    private async Task<bool> AssertExternalUnavailableAsync(OpenCodeClient client, string arm, CancellationToken cancellationToken)
    {
        if (server.RpcPlugin is not null)
        {
            return false;
        }
        await Assert.That(server.IsExternal).IsTrue();
        var failure = await Assert.That(() => client.Rpc.CallAsync(RpcFacadeData.Echo, RpcFacadeData.EchoInput,
            cancellationToken: cancellationToken)).ThrowsExactly<OpenCodeRpcException>();
        await Assert.That(failure?.ErrorType).IsEqualTo(RpcFacadeData.UnavailableType);
        await Assert.That(failure?.Status).IsEqualTo(400);
        await Assert.That(failure?.Message).IsEqualTo(RpcFacadeData.UnavailableMessage);
        Console.WriteLine("rpc-facade-live: mode=external arm=rpc.unavailable requested-arm=" + arm);
        return true;
    }
}
