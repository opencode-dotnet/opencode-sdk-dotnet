using System.Net;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Server;

public sealed class ServerClientContractTests
{
    [Test]
    public async Task GetInfoAsync_Should_Return_The_Typed_Urls()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.InfoOk);

        var response = await scenario.Client.Server.GetInfoAsync();

        await Assert.That(response.ServerInfo.Urls.Single()).IsEqualTo("http://localhost:4096");
        await Assert.That(scenario.Requests.Single().RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/info"));
        await Assert.That(scenario.Requests.Single().Method).IsEqualTo(HttpMethod.Get);
    }
}
