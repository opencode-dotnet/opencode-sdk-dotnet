using System.Net.WebSockets;
using System.Text.Json.Nodes;
using OpenCode.Sdk.Tests.Experimental.Support;
using OpenCode.Sdk.Tests.Harness;

namespace OpenCode.Sdk.Tests.Experimental;

[ClassDataSource<SimulatedDriveServerFixture>(Shared = SharedType.PerTestSession)]
[NotInParallel(ParallelConstraintKeys.ServerProcess)]
public sealed class ClientMcpTokenLiveTests(SimulatedDriveServerFixture server)
{
    [Test]
    [Timeout(60_000)]
    public async Task CreateClientMcpConnectTokenAsync_Should_Attach_Once_Only_For_Its_Name_And_Location(CancellationToken cancellationToken)
    {
        using var workspace = server.CreateWorkspace();
        using var otherWorkspace = server.CreateWorkspace();
        var location = new LocationSelector { Directory = workspace.Path };
        using var client = server.CreateClient();
        var request = new ExperimentalMcpClientTokenRequest { Name = "ticketed", Location = location };
        var token = await client.Experimental.CreateClientMcpConnectTokenAsync(request, cancellationToken: cancellationToken);
        using var attached = new ClientMcpLiveWire(server, location);
        // This overload never adds Authorization: only the minted ticket can admit the socket.
        await Assert.That(() => attached.ConnectAsync("ticketed", token.ConnectToken.Ticket, cancellationToken)).ThrowsNothing();
        await Assert.That(attached.Socket.State).IsEqualTo(WebSocketState.Open);
#if NET
        await Assert.That(attached.Socket.HttpStatusCode).IsEqualTo(System.Net.HttpStatusCode.SwitchingProtocols);
#endif
        var initialize = await attached.ReadAsync(cancellationToken);
        await Assert.That(initialize!["method"]!.GetValue<string>()).IsEqualTo("initialize");
        var response = JsonNode.Parse(new FixtureLoader().LoadJson("Experimental.mcp-initialize.json"))![0]!["response"]!.AsObject();
        response["result"]!["protocolVersion"] = initialize["params"]!["protocolVersion"]!.DeepClone();
        await attached.ReplyAsync(initialize, response, cancellationToken);
        await attached.AssertInitializedAndListToolsAsync(cancellationToken);

        await AssertRefusedAsync(token.ConnectToken.Ticket, "ticketed", location, cancellationToken);
        var wrongName = await client.Experimental.CreateClientMcpConnectTokenAsync(request, cancellationToken: cancellationToken);
        await AssertRefusedAsync(wrongName.ConnectToken.Ticket, "other", location, cancellationToken);
        var wrongDirectory = await client.Experimental.CreateClientMcpConnectTokenAsync(request, cancellationToken: cancellationToken);
        await AssertRefusedAsync(wrongDirectory.ConnectToken.Ticket, "ticketed",
            new LocationSelector { Directory = otherWorkspace.Path }, cancellationToken);
    }

    private async Task AssertRefusedAsync(string ticket, string name, LocationSelector location, CancellationToken cancellationToken)
    {
        using var refused = new ClientMcpLiveWire(server, location);
        _ = await Assert.That(() => refused.ConnectAsync(name, ticket, cancellationToken)).ThrowsExactly<WebSocketException>();
#if NET
        await Assert.That(refused.Socket.HttpStatusCode).IsEqualTo(System.Net.HttpStatusCode.Forbidden);
#endif
    }
}
