using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Providers;

public sealed class ProvidersClientContractTests
{
    [Test]
    public async Task GetProviderAsync_Should_Take_The_Id_As_An_Argument_And_Throw_The_Declared_404()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.ProviderNotFoundError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Providers.GetProviderAsync("prov_9"))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<ProviderNotFoundError>();
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/provider/prov_9"));
    }
}
