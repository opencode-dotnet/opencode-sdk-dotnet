using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;
using OpenCode.Sdk.TestSupport;

namespace OpenCode.Sdk.Tests;

public sealed class VcsClientContractTests
{
    [Test]
    public async Task GetStatusAsync_Should_Return_The_Typed_Changes_With_Their_Location()
    {
        const string changes = "[{\"file\":\"a.txt\",\"additions\":3,\"deletions\":0,\"status\":\"added\"},"
            + "{\"file\":\"b.txt\",\"additions\":1,\"deletions\":2,\"status\":\"modified\"}]";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(changes));

        var response = await scenario.Client.Vcs.GetStatusAsync();

        await Assert.That(response.Changes.Count).IsEqualTo(2);
        await Assert.That(response.Changes[0].File).IsEqualTo("a.txt");
        await Assert.That(response.Changes[0].Status).IsEqualTo(VcsFileStatusStatus.Added);
        await Assert.That(response.Changes[1].File).IsEqualTo("b.txt");
        await Assert.That(response.Changes[1].Additions).IsEqualTo(1);
        await Assert.That(response.Changes[1].Deletions).IsEqualTo(2);
        await Assert.That(response.Changes[1].Status).IsEqualTo(VcsFileStatusStatus.Modified);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/vcs/status"));
    }

    [Test]
    public async Task GetStatusAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Vcs.GetStatusAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task GetStatusAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Vcs.GetStatusAsync(requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task ListBranchesAsync_Should_Return_The_Typed_Branches_With_Their_Location()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK, WireBodyData.LocationEnvelope("[\"main\",\"dev\"]"));

        var response = await scenario.Client.Vcs.ListBranchesAsync();

        await Assert.That(response.Branches.Count).IsEqualTo(2);
        await Assert.That(response.Branches[0]).IsEqualTo("main");
        await Assert.That(response.Branches[1]).IsEqualTo("dev");
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/vcs/branch"));
    }

    [Test]
    public async Task ListBranchesAsync_Should_Return_An_Empty_List_With_Its_Location()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("[]"));

        var response = await scenario.Client.Vcs.ListBranchesAsync();

        await Assert.That(response.Branches.Count).IsEqualTo(0);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
    }

    [Test]
    public async Task ListBranchesAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Vcs.ListBranchesAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task ListBranchesAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Vcs.ListBranchesAsync(requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task GetBaseAsync_Should_Return_The_Typed_Base_With_Its_Location()
    {
        const string reviewBase = "{\"name\":\"main\",\"ref\":\"a1b2c3\",\"source\":\"reflog\"}";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope(reviewBase));

        var response = await scenario.Client.Vcs.GetBaseAsync();

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Base!.Name).IsEqualTo("main");
        await Assert.That(response.Base.Ref).IsEqualTo("a1b2c3");
        await Assert.That(response.Base.Source).IsEqualTo(VcsBaseSource.Reflog);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/vcs/base"));
    }

    [Test]
    public async Task GetBaseAsync_Should_Return_Null_When_No_Base_Is_Inferable()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("null"));

        var response = await scenario.Client.Vcs.GetBaseAsync();

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Base).IsNull();
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
    }

    [Test]
    public async Task GetBaseAsync_Should_Throw_The_Declared_503_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.ServiceUnavailable, WireBodyData.ServiceUnavailableError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Vcs.GetBaseAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(503);
        await Assert.That(exception.Error).IsTypeOf<ServiceUnavailableError>();
    }

    [Test]
    public async Task GetBaseAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Vcs.GetBaseAsync(requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task GetDiffAsync_Should_Return_The_Typed_Diffs_With_Their_Location()
    {
        var diff = new FixtureLoader().LoadJson("Serialization.known-diff-status.json");
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope($"[{diff}]"));

        var response = await scenario.Client.Vcs.GetDiffAsync(new VcsDiffRequest { Mode = VcsMode.Working });

        await Assert.That(response.Diffs.Count).IsEqualTo(1);
        await Assert.That(response.Diffs[0].File).IsEqualTo("src/App.cs");
        await Assert.That(response.Diffs[0].Additions).IsEqualTo(1);
        await Assert.That(response.Diffs[0].Status).IsEqualTo(FileDiffInfoStatus.Modified);
        await Assert.That(response.Location.Directory).IsEqualTo(WireBodyData.ResolvedDirectory);
        await Assert.That(scenario.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/vcs/diff?mode=working");
    }

    [Test]
    public async Task GetDiffAsync_Should_Send_The_Committed_Mode_With_Its_Base_And_Context()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.LocationEnvelope("[]"));

        var response = await scenario.Client.Vcs.GetDiffAsync(new VcsDiffRequest
        {
            Mode = VcsMode.Committed,
            Base = "main",
            Context = "3",
        });

        await Assert.That(response.Diffs.Count).IsEqualTo(0);
        await Assert.That(scenario.Requests.Single().RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/vcs/diff?mode=committed&base=main&context=3");
    }

    [Test]
    public async Task GetDiffAsync_Should_Throw_The_Declared_503_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.ServiceUnavailable, WireBodyData.ServiceUnavailableError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Vcs.GetDiffAsync(new VcsDiffRequest { Mode = VcsMode.Branch }))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(503);
        await Assert.That(exception.Error).IsTypeOf<ServiceUnavailableError>();
    }

    [Test]
    public async Task GetDiffAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Vcs.GetDiffAsync(
            new VcsDiffRequest { Mode = VcsMode.Working },
            OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task InitializeRepositoryAsync_Should_Post_The_Provider_And_Location_And_Accept_No_Content()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Vcs.InitializeRepositoryAsync(new VcsInitRequest
        {
            Location = new LocationSelector { Directory = "/repo" },
            Provider = "hg",
        });

        await Assert.That(response.IsError).IsFalse();
        await Assert.That(response.Status).IsEqualTo(204);
        var recorded = scenario.Requests.Single();
        await Assert.That(recorded.Method.Method).IsEqualTo("POST");
        await Assert.That(recorded.RequestUri!.AbsoluteUri)
            .IsEqualTo("http://localhost:4096/api/vcs/init?location[directory]=%2Frepo&provider=hg");
    }

    [Test]
    public async Task InitializeRepositoryAsync_Should_Throw_The_Declared_501_Error()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.NotImplemented,
            "{\"_tag\":\"VcsInitNotSupportedError\",\"providerID\":\"svn\",\"message\":\"svn cannot initialize\"}");

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Vcs.InitializeRepositoryAsync(new VcsInitRequest { Provider = "svn" }))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(501);
        await Assert.That(exception.Error).IsTypeOf<VcsInitNotSupportedError>();
        await Assert.That(((VcsInitNotSupportedError)exception.Error!).ProviderId).IsEqualTo("svn");
    }

    [Test]
    public async Task GetStatusAsync_Should_Return_The_404_Missing_Location_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.NotFound,
            "{\"_tag\":\"LocationNotFoundError\",\"location\":{\"directory\":\"/gone\"},\"message\":\"Location not found: /gone\"}");

        var response = await scenario.Client.Vcs.GetStatusAsync(
            new VcsStatusRequest { Location = new LocationSelector { Directory = "/gone" } },
            OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(404);
        await Assert.That(response.Error).IsTypeOf<LocationNotFoundError>();
        await Assert.That(((LocationNotFoundError)response.Error!).Location.Directory).IsEqualTo("/gone");
    }
}
