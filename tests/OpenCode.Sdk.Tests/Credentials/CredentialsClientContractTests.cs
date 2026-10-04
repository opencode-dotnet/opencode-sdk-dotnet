using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests;

public sealed class CredentialsClientContractTests
{
    private const string KeyEntry =
        "{\"id\":\"cred_1\",\"integrationID\":\"openai\",\"label\":\"work\",\"active\":true,"
        + "\"value\":{\"type\":\"key\",\"key\":\"sk-test\"}}";

    private const string OAuthEntry =
        "{\"id\":\"cred_2\",\"integrationID\":\"github\",\"label\":\"personal\",\"active\":false,"
        + "\"value\":{\"type\":\"oauth\",\"methodID\":\"device\",\"refresh\":\"r-1\",\"access\":\"a-1\",\"expires\":1700000000000}}";

    [Test]
    public async Task CreateCredentialAsync_Should_Post_The_Typed_Body_On_The_Collection_Route()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(KeyEntry));

        var response = await scenario.Client.Credentials.CreateCredentialAsync(new CredentialCreateRequest
        {
            IntegrationId = "openai",
            Label = "work",
            Value = new CredentialKey { Key = "sk-test" },
            Activate = true,
        });

        await Assert.That(response.Status).IsEqualTo(200);
        await Assert.That(response.Credential.Id).IsEqualTo("cred_1");
        await Assert.That(response.Credential.Active).IsTrue();
        await Assert.That(response.Credential.Value).IsTypeOf<CredentialKey>();
        await Assert.That(((CredentialKey)response.Credential.Value).Key).IsEqualTo("sk-test");
        var request = scenario.Requests.Single();
        await Assert.That(request.Method.Method).IsEqualTo("POST");
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/credential"));
        await Assert.That(request.Body).IsEqualTo(
            "{\"integrationID\":\"openai\",\"label\":\"work\",\"value\":{\"type\":\"key\",\"key\":\"sk-test\"},\"activate\":true}");
    }

    [Test]
    public async Task CreateCredentialAsync_Should_Return_The_Declared_409_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Conflict, WireBodyData.ConflictError);

        var response = await scenario.Client.Credentials.CreateCredentialAsync(
            new CredentialCreateRequest
            {
                Id = "cred_1",
                IntegrationId = "openai",
                Value = new CredentialKey { Key = "sk-test" },
            },
            OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(409);
        await Assert.That(response.Error).IsTypeOf<ConflictError>();
    }

    [Test]
    public async Task CreateCredentialAsync_Should_Send_A_Caller_Chosen_Id()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(KeyEntry));

        _ = await scenario.Client.Credentials.CreateCredentialAsync(new CredentialCreateRequest
        {
            Id = "cred_1",
            IntegrationId = "openai",
            Value = new CredentialKey { Key = "sk-test" },
        });

        await Assert.That(scenario.Requests.Single().Body).IsEqualTo(
            "{\"id\":\"cred_1\",\"integrationID\":\"openai\",\"value\":{\"type\":\"key\",\"key\":\"sk-test\"}}");
    }

    [Test]
    public async Task ListCredentialsAsync_Should_Read_Both_Value_Arms()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK,
            WireBodyData.Envelope("[" + KeyEntry + "," + OAuthEntry + "]"));

        var response = await scenario.Client.Credentials.ListCredentialsAsync();

        await Assert.That(response.Credentials).Count().IsEqualTo(2);
        await Assert.That(response.Credentials[0].Value).IsTypeOf<CredentialKey>();
        var oauth = response.Credentials[1].Value as CredentialOAuth;
        await Assert.That(oauth).IsNotNull();
        await Assert.That(oauth!.MethodId).IsEqualTo("device");
        await Assert.That(oauth.Access).IsEqualTo("a-1");
        await Assert.That(oauth.Expires).IsEqualTo(1_700_000_000_000L);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method.Method).IsEqualTo("GET");
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/credential"));
        await Assert.That(request.Body).IsNull();
    }

    [Test]
    public async Task ListCredentialsAsync_Should_Throw_The_Declared_401_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Credentials.ListCredentialsAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(401);
        await Assert.That(exception.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task UpdateCredentialAsync_Should_Send_The_Patch_Body_On_The_Flat_Id_Route()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Credentials.UpdateCredentialAsync("cred_1", new CredentialUpdateRequest
        {
            Label = "work laptop",
        });

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method.Method).IsEqualTo("PATCH");
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/credential/cred_1"));
        await Assert.That(request.Body).IsEqualTo("{\"label\":\"work laptop\"}");
    }

    [Test]
    public async Task ActivateCredentialAsync_Should_Post_On_The_Flat_Id_Route()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Credentials.ActivateCredentialAsync("cred_1");

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method.Method).IsEqualTo("POST");
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/credential/cred_1/activate"));
    }

    [Test]
    public async Task ActivateCredentialAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Credentials.ActivateCredentialAsync("cred_1"))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task ActivateCredentialAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Credentials.ActivateCredentialAsync(
            "cred_1", requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }
}
