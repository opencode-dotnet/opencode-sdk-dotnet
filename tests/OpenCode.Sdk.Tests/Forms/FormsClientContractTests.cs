using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Forms;

public sealed class FormsClientContractTests
{
    [Test]
    public async Task ListFormsAsync_Should_Return_The_Typed_Pending_Forms()
    {
        const string form = "{\"id\":\"frm_1\",\"sessionID\":\"ses_1\",\"title\":\"Pick a provider\","
            + "\"fields\":[{\"key\":\"provider\",\"type\":\"string\"}]}";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope($"[{form}]"));

        var response = await scenario.Client.Forms.ListFormsAsync();

        var pending = response.Forms.Single();
        await Assert.That(pending.Id).IsEqualTo("frm_1");
        await Assert.That(pending.SessionId).IsEqualTo("ses_1");
        await Assert.That(pending.Title).IsEqualTo("Pick a provider");
        var field = (FormStringField)pending.Fields.Single();
        await Assert.That(field.Key).IsEqualTo("provider");
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/form"));
    }

    [Test]
    public async Task ListFormsAsync_Should_Return_An_Empty_List()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("[]"));

        var response = await scenario.Client.Forms.ListFormsAsync();

        await Assert.That(response.Forms).IsEmpty();
    }

    [Test]
    public async Task ListFormsAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Forms.ListFormsAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task ListFormsAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Forms.ListFormsAsync(requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }
}
