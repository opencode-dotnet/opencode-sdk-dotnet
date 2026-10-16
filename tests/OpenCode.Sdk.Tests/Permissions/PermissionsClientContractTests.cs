using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Harness;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Permissions;

public sealed class PermissionsClientContractTests
{
    [Test]
    public async Task ListRequestsAsync_Should_Return_The_Typed_Pending_Requests()
    {
        var payload = new FixtureLoader().LoadJson("Serialization.known-permission-request.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope($"[{payload}]"));

        var response = await scenario.Client.Permissions.ListRequestsAsync();

        var request = response.Requests.Single();
        await Assert.That(request.Id).IsEqualTo("per_1");
        await Assert.That(request.Action).IsEqualTo("read");
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/permission/request"));
    }

    [Test]
    public async Task ListSavedAsync_Should_Return_The_Typed_Saved_Permissions()
    {
        const string saved = "[{\"id\":\"per_1\",\"projectID\":\"prj_1\",\"action\":\"read\",\"resource\":\"fs\",\"time\":{\"created\":1,\"updated\":2}}]";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope(saved));

        var response = await scenario.Client.Permissions.ListSavedAsync();

        var permission = response.Saved.Single();
        await Assert.That(permission.Id).IsEqualTo("per_1");
        await Assert.That(permission.ProjectId).IsEqualTo("prj_1");
        await Assert.That(permission.Action).IsEqualTo("read");
        await Assert.That(permission.Resource).IsEqualTo("fs");
        await Assert.That(permission.Time.Created).IsEqualTo(1);
        await Assert.That(permission.Time.Updated).IsEqualTo(2);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/permission/saved"));
    }

    [Test]
    public async Task ListSavedAsync_Should_Filter_By_ProjectId_And_Return_An_Empty_List()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Envelope("[]"));

        var response = await scenario.Client.Permissions.ListSavedAsync(new PermissionSavedListRequest { ProjectId = "prj_9" });

        await Assert.That(response.Saved).IsEmpty();
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/permission/saved?projectID=prj_9"));
    }

    [Test]
    public async Task ListSavedAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Permissions.ListSavedAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task ListSavedAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Permissions.ListSavedAsync(requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task RemoveSavedAsync_Should_Take_The_Id_As_An_Argument_On_The_204()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Permissions.RemoveSavedAsync("per_9");

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/permission/saved/per_9"));
    }
}
