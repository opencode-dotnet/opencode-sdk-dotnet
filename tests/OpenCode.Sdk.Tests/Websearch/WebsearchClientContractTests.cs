using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests.Websearch;

public sealed class WebsearchClientContractTests
{
    [Test]
    public async Task QueryAsync_Should_Send_The_Typed_Body_And_Return_The_Typed_Results()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-websearch-response.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(payload));

        var response = await scenario.Client.Websearch.QueryAsync(new WebsearchQueryRequest
        {
            Query = "opencode",
        });

        await Assert.That(response.Query.ProviderId).IsEqualTo("exa");
        await Assert.That(response.Query.Results.Single().Url).IsEqualTo("https://example.com/a");
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/websearch"));
        await Assert.That(request.Body).IsEqualTo("{\"query\":\"opencode\"}");
    }
}
