using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Integrations;

public sealed class IntegrationsClientContractTests
{
    [Test]
    [Arguments(" ")]
    [Arguments(".")]
    [Arguments("..")]
    public async Task GetIntegrationClient_Should_Refuse_An_Id_The_Route_Would_Refuse(string integrationId)
    {
        using var scenario = ContractScenario.Responding();

        var exception = Assert.Throws<ArgumentException>(() => _ = scenario.Client.Integrations.GetIntegrationClient(integrationId));

        await Assert.That(exception.ParamName).IsEqualTo("integrationId");
    }

    [Test]
    public async Task ListIntegrationsAsync_Should_Return_The_Typed_Integrations()
    {
        const string integration = "{\"id\":\"int_1\",\"name\":\"GitHub\",\"methods\":[],\"connections\":[]}";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope($"[{integration}]"));

        var response = await scenario.Client.Integrations.ListIntegrationsAsync();

        var single = response.Integrations.Single();
        await Assert.That(single.Id).IsEqualTo("int_1");
        await Assert.That(single.Name).IsEqualTo("GitHub");
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/integration"));
    }

    [Test]
    public async Task ListIntegrationsAsync_Should_Return_An_Empty_List()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("[]"));

        var response = await scenario.Client.Integrations.ListIntegrationsAsync();

        await Assert.That(response.Integrations).IsEmpty();
    }

    [Test]
    public async Task ListIntegrationsAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Integrations.ListIntegrationsAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task ListIntegrationsAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Integrations.ListIntegrationsAsync(requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }
}
