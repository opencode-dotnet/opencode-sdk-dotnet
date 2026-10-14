using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Agents;

public sealed class AgentsClientContractTests
{
    [Test]
    public async Task GetAgentAsync_Should_Take_The_Id_As_An_Argument_And_Throw_The_Declared_404()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.AgentNotFoundError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Agents.GetAgentAsync("missing"))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<AgentNotFoundError>();
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/agent/missing"));
    }
}
