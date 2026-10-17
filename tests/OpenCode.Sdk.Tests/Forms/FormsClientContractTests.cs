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
}
