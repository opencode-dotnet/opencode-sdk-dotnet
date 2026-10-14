using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Generation;

public sealed class ExperimentalGenerationClientContractTests
{
    [Test]
    public async Task GenerateTextAsync_Should_Send_The_Prompt_And_Return_The_Typed_Text()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK,
            WireBodyData.LocationEnvelope("{\"text\":\"hello\"}"));

        var response = await scenario.Client.Experimental.GenerateTextAsync(new ExperimentalGenerateTextRequest
        {
            Prompt = "say hello",
        });

        await Assert.That(response.GenerateText.Text).IsEqualTo("hello");
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/experimental/generate"));
        await Assert.That(request.Body).IsEqualTo("{\"prompt\":\"say hello\"}");
    }
}
