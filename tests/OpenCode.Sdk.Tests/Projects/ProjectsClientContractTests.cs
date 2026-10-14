using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Projects;

public sealed class ProjectsClientContractTests
{
    [Test]
    public async Task GetLocationAsync_Should_Return_The_Typed_Project()
    {
        using var scenario = ContractScenario.Responding(
            HttpStatusCode.OK,
            WireBodyData.ResolvedLocation);

        var response = await scenario.Client.GetLocationAsync();

        await Assert.That(response.ResolvedLocation.Project.Id).IsEqualTo("prj_1");
        await Assert.That(response.ResolvedLocation.Project.Directory).IsEqualTo("/repo");
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/location"));
    }

    [Test]
    public async Task ListProjectsAsync_Should_Return_The_Typed_Projects()
    {
        const string body = "[{\"id\":\"prj_1\",\"canonical\":\"/repo\",\"time\":{\"created\":1,\"updated\":2,\"active\":3},\"sandboxes\":[]}]";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, body);

        var response = await scenario.Client.Projects.ListProjectsAsync();

        var project = response.Projects.Single();
        await Assert.That(project.Id).IsEqualTo("prj_1");
        await Assert.That(project.Canonical).IsEqualTo("/repo");
        await Assert.That(project.Time.Created).IsEqualTo(1);
        await Assert.That(project.Time.Active).IsEqualTo(3);
        await Assert.That(project.Sandboxes).IsEmpty();
        await Assert.That(project.Time.Archived).IsNull();
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/project"));
    }

    [Test]
    public async Task ListProjectsAsync_Should_Send_The_Archived_Filter_And_Read_When_A_Project_Was_Archived()
    {
        const string body = "[{\"id\":\"prj_1\",\"canonical\":\"/gone\",\"time\":{\"created\":1,\"updated\":2,\"active\":3,\"archived\":4},\"sandboxes\":[]}]";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, body);

        var response = await scenario.Client.Projects.ListProjectsAsync(new ProjectListRequest { Archived = QueryBoolean.True });

        await Assert.That(response.Projects.Single().Time.Archived).IsEqualTo(4);
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/project?archived=true"));
    }

    [Test]
    public async Task ListProjectsAsync_Should_Return_An_Empty_List()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, "[]");

        var response = await scenario.Client.Projects.ListProjectsAsync();

        await Assert.That(response.Projects).IsEmpty();
    }

    [Test]
    public async Task ListProjectsAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Projects.ListProjectsAsync())
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task ListProjectsAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Projects.ListProjectsAsync(requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task UpdateProjectAsync_Should_Send_The_Typed_Body_And_Return_The_Updated_Project()
    {
        const string project = "{\"id\":\"prj_1\",\"canonical\":\"/repo\",\"name\":\"renamed\","
            + "\"time\":{\"created\":1,\"updated\":2,\"active\":3},\"sandboxes\":[]}";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, project);

        var response = await scenario.Client.Projects.UpdateProjectAsync("prj_1", new ProjectUpdateRequest
        {
            Name = "renamed",
        });

        await Assert.That(response.Project.Id).IsEqualTo("prj_1");
        await Assert.That(response.Project.Name).IsEqualTo("renamed");
        var request = scenario.Requests.Single();
        await Assert.That(request.Method.Method).IsEqualTo("PATCH");
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/project/prj_1"));
        await Assert.That(request.Body).IsEqualTo("{\"name\":\"renamed\"}");
    }

    [Test]
    public async Task UpdateProjectAsync_Should_Send_An_Empty_Body_When_Omitted()
    {
        const string project = "{\"id\":\"prj_1\",\"canonical\":\"/repo\",\"time\":{\"created\":1,\"updated\":2,\"active\":3},\"sandboxes\":[]}";
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, project);

        _ = await scenario.Client.Projects.UpdateProjectAsync("prj_1");

        await Assert.That(scenario.Requests.Single().Body).IsEqualTo("{}");
    }

    [Test]
    public async Task UpdateProjectAsync_Should_Throw_The_Declared_404_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.ProjectNotFoundError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Projects.UpdateProjectAsync("prj_9"))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(404);
        await Assert.That(exception.Error).IsTypeOf<ProjectNotFoundError>();
    }

    [Test]
    public async Task UpdateProjectAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Projects.UpdateProjectAsync("prj_1", requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }
}
