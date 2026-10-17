using System.Net;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Debug;

public sealed class DebugClientContractTests
{
    [Test]
    public async Task ListLocationsAsync_Should_Return_The_Typed_Locations()
    {
        const string body = "[{\"directory\":\"/repo\",\"workspaceID\":\"wrk_1\"},{\"directory\":\"/other\"}]";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, body);

        var response = await scenario.Client.Debug.ListLocationsAsync();

        await Assert.That(response.Locations.Count).IsEqualTo(2);
        await Assert.That(response.Locations[0].Directory).IsEqualTo("/repo");
        await Assert.That(response.Locations[1].Directory).IsEqualTo("/other");
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/debug/location"));
    }

    [Test]
    public async Task ListLocationsAsync_Should_Return_An_Empty_List()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, "[]");

        var response = await scenario.Client.Debug.ListLocationsAsync();

        await Assert.That(response.Locations).IsEmpty();
    }
}
