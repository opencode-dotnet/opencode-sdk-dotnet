using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Integrations;

public sealed class IntegrationClientContractTests
{
    [Test]
    public async Task GetCommandStatusAsync_Should_Materialize_The_Tagged_Status_Behind_Both_Path_Parameters()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-command-attempt-complete.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(payload));

        var response = await scenario.Client.Integrations.GetIntegrationClient("int_1").GetCommandStatusAsync("att_1");

        await Assert.That(response.CommandStatus).IsTypeOf<IntegrationCommandAttemptStatusComplete>();
        var complete = (IntegrationCommandAttemptStatusComplete)response.CommandStatus;
        await Assert.That(complete.Time.Created).IsEqualTo(1755200000);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/integration/int_1/connect/command/att_1"));
    }

    [Test]
    public async Task GetAsync_Should_Return_The_Typed_Integration()
    {
        const string integration = "{\"id\":\"int_1\",\"name\":\"GitHub\",\"methods\":[],\"connections\":[]}";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(integration));

        var response = await scenario.Client.Integrations.GetIntegrationClient("int_1").GetAsync();

        await Assert.That(response.Integration.Id).IsEqualTo("int_1");
        await Assert.That(response.Integration.Name).IsEqualTo("GitHub");
        await Assert.That(response.Integration.Methods).IsEmpty();
        await Assert.That(response.Integration.Connections).IsEmpty();
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/integration/int_1"));
    }

    [Test]
    public async Task GetAsync_Should_Throw_The_Declared_404_When_The_Integration_Is_Unknown()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.IntegrationNotFoundError);

        var exception = await Assert.That(async () =>
            _ = await scenario.Client.Integrations.GetIntegrationClient("int_9").GetAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<IntegrationNotFoundError>();
        await Assert.That(((IntegrationNotFoundError)exception.Error!).IntegrationId).IsEqualTo("int_9");
    }

    [Test]
    public async Task ConnectWithKeyAsync_Should_Send_The_Typed_Body_On_The_Handle_Route()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Integrations.GetIntegrationClient("int_1")
            .ConnectWithKeyAsync(new IntegrationConnectKeyRequest { Key = "sk-test" });

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
        var request = scenario.Requests.Single();
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/integration/int_1/connect/key"));
        await Assert.That(request.Body).IsEqualTo("{\"key\":\"sk-test\"}");
    }

    [Test]
    public async Task BeginOAuthConnectionAsync_Should_Return_The_Typed_Attempt()
    {
        const string attempt = "{\"attemptID\":\"att_1\",\"url\":\"https://example.test/authorize\","
            + "\"instructions\":\"Open the link\",\"mode\":\"auto\",\"time\":{\"created\":1,\"expires\":2}}";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(attempt));

        var response = await scenario.Client.Integrations.GetIntegrationClient("int_1")
            .BeginOAuthConnectionAsync(new IntegrationOAuthConnectRequest { MethodId = "github" });

        await Assert.That(response.OAuthConnect.AttemptId).IsEqualTo("att_1");
        await Assert.That(response.OAuthConnect.Url).IsEqualTo("https://example.test/authorize");
        await Assert.That(response.OAuthConnect.Mode).IsEqualTo(IntegrationAttemptMode.Auto);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/integration/int_1/connect/oauth"));
        await Assert.That(scenario.Requests.Single().Body).IsEqualTo("{\"methodID\":\"github\"}");
    }

}
