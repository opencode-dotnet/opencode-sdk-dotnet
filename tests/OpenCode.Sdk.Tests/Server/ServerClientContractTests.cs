using System.Net;
using OpenCode.Sdk.Models;
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

    [Test]
    public async Task GetInfoAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Server.GetInfoAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task GetInfoAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Server.GetInfoAsync(OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }
}
