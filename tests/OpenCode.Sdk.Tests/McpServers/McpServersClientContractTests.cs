using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.McpServers;

public sealed class McpServersClientContractTests
{
    [Test]
    public async Task ListMcpServersAsync_Should_Return_The_Typed_Servers()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-mcp-server.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope($"[{payload}]"));

        var response = await scenario.Client.McpServers.ListMcpServersAsync();

        var server = response.McpServers.Single();
        await Assert.That(server.Name).IsEqualTo("docs");
        await Assert.That(server.Status).IsTypeOf<McpStatusConnected>();
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/mcp"));
    }

    [Test]
    public async Task RemoveMcpServerAsync_Should_Throw_The_Declared_404_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.McpServerNotFoundError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Experimental.RemoveMcpServerAsync("docs"))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<McpServerNotFoundError>();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/experimental/mcp/docs"));
    }

    [Test]
    public async Task PostConnectAsync_Should_Send_A_Bodiless_Post()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Experimental.ConnectMcpServerAsync("docs");

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/experimental/mcp/docs/connect"));
        await Assert.That(request.Body).IsNull();
        await Assert.That(request.ContentType).IsNull();
    }

    [Test]
    public async Task PutAddAsync_Should_Send_The_Typed_Config_On_The_Put()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Experimental.AddMcpServerAsync("docs", new ExperimentalMcpAddRequest
        {
            Config = new McpLocalConfig { Command = ["docs-server"] },
        });

        await Assert.That(response.Status).IsEqualTo(204);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Put);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/experimental/mcp/docs"));
        await Assert.That(request.Body).IsEqualTo("{\"config\":{\"type\":\"local\",\"command\":[\"docs-server\"]}}");
    }
}
