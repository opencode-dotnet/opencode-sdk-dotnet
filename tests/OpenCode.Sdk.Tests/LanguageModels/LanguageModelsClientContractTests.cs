using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.LanguageModels;

public sealed class LanguageModelsClientContractTests
{
    private const string KnownModel = "{\"id\":\"anthropic/claude-3-5-sonnet\",\"modelID\":\"claude-3-5-sonnet\","
        + "\"providerID\":\"anthropic\",\"name\":\"Claude 3.5 Sonnet\","
        + "\"capabilities\":{\"tools\":true,\"input\":[\"text\"],\"output\":[\"text\"]},"
        + "\"variants\":[{\"id\":\"default\"}],\"time\":{\"released\":1710000000},"
        + "\"cost\":[{\"input\":3,\"output\":15,\"cache\":{\"read\":0.3,\"write\":3.75}}],"
        + "\"status\":\"active\",\"enabled\":true,\"limit\":{\"context\":200000,\"output\":8192}}";

    [Test]
    public async Task GetDefaultAsync_Should_Return_The_Typed_Default_Model()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(KnownModel));

        var response = await scenario.Client.LanguageModels.GetDefaultAsync();

        await Assert.That(response.Default!.Id).IsEqualTo("anthropic/claude-3-5-sonnet");
        await Assert.That(response.Default.ModelId).IsEqualTo("claude-3-5-sonnet");
        await Assert.That(response.Default.ProviderId).IsEqualTo("anthropic");
        await Assert.That(response.Default.Status).IsEqualTo(ModelInfoStatus.Active);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/model/default"));
    }

    [Test]
    public async Task GetDefaultAsync_Should_Return_Null_When_No_Model_Is_Selected()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("null"));

        var response = await scenario.Client.LanguageModels.GetDefaultAsync();

        await Assert.That(response.Default).IsNull();
    }

    [Test]
    public async Task GetDefaultAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.LanguageModels.GetDefaultAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task GetDefaultAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.LanguageModels.GetDefaultAsync(requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task ListModelsAsync_Should_Return_The_Typed_Models_With_Their_Location()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope($"[{KnownModel}]"));

        var response = await scenario.Client.LanguageModels.ListModelsAsync();

        await Assert.That(response.Models.Count).IsEqualTo(1);
        await Assert.That(response.Models[0].Id).IsEqualTo("anthropic/claude-3-5-sonnet");
        await Assert.That(response.Models[0].ModelId).IsEqualTo("claude-3-5-sonnet");
        await Assert.That(response.Models[0].ProviderId).IsEqualTo("anthropic");
        await Assert.That(response.Models[0].Status).IsEqualTo(ModelInfoStatus.Active);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/model"));
    }

    [Test]
    public async Task ListModelsAsync_Should_Return_An_Empty_List_With_Its_Location()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("[]"));

        var response = await scenario.Client.LanguageModels.ListModelsAsync();

        await Assert.That(response.Models.Count).IsEqualTo(0);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
    }

    [Test]
    public async Task ListModelsAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.LanguageModels.ListModelsAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task ListModelsAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.LanguageModels.ListModelsAsync(requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }
}
