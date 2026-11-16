using System.Net.WebSockets;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Tests.Experimental.Support;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Experimental;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class ClientMcpLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task ConnectClientMcp_Should_Observe_Server_Attach_Failure_And_Reload(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        var location = new LocationSelector { Directory = workspace.Path };
        using var client = server.CreateClient(location);
        using (var rejected = new ClientMcpLiveWire(server, location))
        {
            await rejected.ConnectAsync(cancellationToken);
            var initialize = await rejected.ReadAsync(cancellationToken);
            await Assert.That(initialize!["method"]!.GetValue<string>()).IsEqualTo("initialize");
            await rejected.ReplyAsync(initialize, new JsonObject
            {
                ["error"] = new JsonObject { ["code"] = -32603, ["message"] = "not today" },
            }, cancellationToken);
            _ = await rejected.ReadAsync(cancellationToken);
            await Assert.That(rejected.Socket.CloseStatus).IsEqualTo(WebSocketCloseStatus.InternalServerError);
            await Assert.That(rejected.Socket.CloseStatusDescription).IsEqualTo("attach failed");
        }

        using var attached = new ClientMcpLiveWire(server, location);
        await attached.ConnectAsync(cancellationToken);
        var request = await attached.ReadAsync(cancellationToken);
        // Receiving initialize proves holdLocation ran; an HTTP 101 alone does not.
        await Assert.That(request!["method"]!.GetValue<string>()).IsEqualTo("initialize");
        await attached.ReplyAsync(request, new JsonObject
        {
            ["result"] = new JsonObject
            {
                ["protocolVersion"] = request["params"]!["protocolVersion"]!.DeepClone(),
                ["capabilities"] = new JsonObject { ["tools"] = new JsonObject() },
                ["serverInfo"] = new JsonObject { ["name"] = "live", ["version"] = "1.0.0" },
            },
        }, cancellationToken);
        await attached.AssertInitializedAndListToolsAsync(cancellationToken);

        _ = await client.ReloadLocationsAsync(cancellationToken: cancellationToken);
        // Invalidation signals shutdown but does not join borrowed scopes
        // (location-services.ts:87-108; Effect RcMap.ts:1027). Do not initiate a competing close.
        // A reload-removed no-op has no finite oracle: LocationResponse has no per-instance field and location.shutdown is asynchronous to the response.
        // The oracle is the exact close; "detected by a bounded wait" is inconclusive, not a kill.
#if NET
        _ = await attached.ReadAsync(cancellationToken);
        await Assert.That(attached.Socket.CloseStatus).IsEqualTo((WebSocketCloseStatus)1012);
        await Assert.That(attached.Socket.CloseStatusDescription).IsEqualTo("location shut down");
#else
        const string nativeLimit = "0x83760002 (native WebSocket component rejects close code 1012)";
        var failure = await Assert.That(async () => _ = await attached.ReadAsync(cancellationToken))
            .ThrowsExactly<WebSocketException>().Because(nativeLimit);
        await Assert.That(failure!.WebSocketErrorCode).IsEqualTo(WebSocketError.NativeError).Because(nativeLimit);
        await Assert.That(failure.NativeErrorCode).IsEqualTo(unchecked((int)0x83760002)).Because(nativeLimit);
        await Assert.That(attached.Socket.State).IsEqualTo(WebSocketState.Aborted).Because(nativeLimit);
#endif
    }
}
