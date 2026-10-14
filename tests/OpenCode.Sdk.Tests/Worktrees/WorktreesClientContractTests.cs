using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Worktrees;

public sealed class WorktreesClientContractTests
{
    [Test]
    public async Task CreateWorktreeAsync_Should_Send_The_Required_Project_When_Options_Are_Omitted()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Worktree);

        var response = await scenario.Client.Worktrees.CreateWorktreeAsync(new WorktreeCreateRequest { ProjectId = "prj_1" });

        await Assert.That(response.Status).IsEqualTo(200);
        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/worktree"));
        await Assert.That(request.Body).IsEqualTo("{\"projectID\":\"prj_1\"}");
    }

    [Test]
    public async Task CreateWorktreeAsync_Should_Send_Optional_Inputs_With_The_Project()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Worktree);

        _ = await scenario.Client.Worktrees.CreateWorktreeAsync(new WorktreeCreateRequest
        {
            ProjectId = "prj_1",
            From = "main",
            Branch = "feature/x",
            Directory = "/repo/feature",
            Name = "feature",
        });

        var request = scenario.Requests.Single();
        await Assert.That(request.RequestUri!.AbsoluteUri).IsEqualTo(
            "http://localhost:4096/api/worktree");
        await Assert.That(request.Body).IsEqualTo(
            "{\"projectID\":\"prj_1\",\"from\":\"main\",\"branch\":\"feature/x\",\"directory\":\"/repo/feature\",\"name\":\"feature\"}");
    }

    [Test]
    public async Task CreateWorktreeAsync_Should_Return_The_Typed_Worktree()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Worktree);

        var response = await scenario.Client.Worktrees.CreateWorktreeAsync(new WorktreeCreateRequest
        {
            ProjectId = "prj_1",
            Directory = "/repo/feature",
            Branch = "feature/x",
        });

        await Assert.That(response.Worktree.Directory).IsEqualTo("/repo/feature");
        var request = scenario.Requests.Single();
        await Assert.That(request.Method.Method).IsEqualTo("POST");
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/worktree"));
        await Assert.That(request.Body)
            .IsEqualTo("{\"projectID\":\"prj_1\",\"branch\":\"feature/x\",\"directory\":\"/repo/feature\"}");
    }

    [Test]
    public async Task CreateWorktreeAsync_Should_Throw_The_Declared_400_Worktree_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.WorktreeError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Worktrees.CreateWorktreeAsync(CreateRequest()))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Message).Contains("'WorktreeError'");
        await Assert.That(exception.Error).IsTypeOf<WorktreeError>();
        var error = (WorktreeError)exception.Error!;
        await Assert.That(error.Data.Message).IsEqualTo("the worktree has uncommitted changes");
        await Assert.That(error.Data.ForceRequired).IsTrue();
    }

    [Test]
    public async Task CreateWorktreeAsync_Should_Throw_The_Other_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Worktrees.CreateWorktreeAsync(CreateRequest()))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task CreateWorktreeAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Worktrees
            .CreateWorktreeAsync(CreateRequest(), OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    /// <summary>
    /// The pinned document declares a required body on this DELETE, so the wire must carry it:
    /// a 204 that arrived without the body would be the server answering a different request.
    /// </summary>
    [Test]
    public async Task RemoveWorktreeAsync_Should_Send_The_Json_Body_On_The_Delete_Route()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Worktrees.RemoveWorktreeAsync(new WorktreeRemoveRequest
        {
            ProjectId = "prj_1",
            Directory = "/repo/feature",
            Force = true,
        });

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method.Method).IsEqualTo("DELETE");
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/worktree"));
        await Assert.That(request.Body).IsEqualTo("{\"projectID\":\"prj_1\",\"directory\":\"/repo/feature\",\"force\":true}");
    }

    [Test]
    public async Task RemoveWorktreeAsync_Should_Throw_The_Declared_400_Worktree_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.WorktreeError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Worktrees.RemoveWorktreeAsync(RemoveRequest()))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Message).Contains("'WorktreeError'");
        await Assert.That(exception.Error).IsTypeOf<WorktreeError>();
        await Assert.That(((WorktreeError)exception.Error!).Data.ForceRequired).IsTrue();
    }

    [Test]
    public async Task RemoveWorktreeAsync_Should_Keep_False_In_The_Body_With_The_Project()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        _ = await scenario.Client.Worktrees.RemoveWorktreeAsync(new WorktreeRemoveRequest
        {
            ProjectId = "prj_1",
            Directory = "/repo/feature",
            Force = false,
        });

        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Delete);
        await Assert.That(request.RequestUri!.AbsoluteUri).IsEqualTo(
            "http://localhost:4096/api/worktree");
        await Assert.That(request.Body).IsEqualTo("{\"projectID\":\"prj_1\",\"directory\":\"/repo/feature\",\"force\":false}");
    }

    [Test]
    public async Task RemoveWorktreeAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Worktrees
            .RemoveWorktreeAsync(RemoveRequest(), OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task RefreshWorktreesAsync_Should_Post_On_The_Refresh_Route()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        var response = await scenario.Client.Worktrees.RefreshWorktreesAsync(new WorktreeRefreshRequest { ProjectId = "prj_1" });

        await Assert.That(response.Status).IsEqualTo(204);
        await Assert.That(response.IsError).IsFalse();
        var request = scenario.Requests.Single();
        await Assert.That(request.Method.Method).IsEqualTo("POST");
        await Assert.That(request.RequestUri).IsEqualTo(new Uri("http://localhost:4096/api/worktree/refresh"));
    }

    [Test]
    public async Task RefreshWorktreesAsync_Should_Throw_The_Declared_400_Worktree_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.WorktreeError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Worktrees.RefreshWorktreesAsync(new WorktreeRefreshRequest { ProjectId = "prj_1" }))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<WorktreeError>();
    }

    [Test]
    public async Task RefreshWorktreesAsync_Should_Send_The_Project()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NoContent, string.Empty);

        _ = await scenario.Client.Worktrees.RefreshWorktreesAsync(new WorktreeRefreshRequest
        {
            ProjectId = "prj_1",
        });

        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Post);
        await Assert.That(request.RequestUri!.AbsoluteUri).IsEqualTo(
            "http://localhost:4096/api/worktree/refresh");
    }

    [Test]
    public async Task RefreshWorktreesAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Worktrees
            .RefreshWorktreesAsync(new WorktreeRefreshRequest { ProjectId = "prj_1" }, requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    [Test]
    public async Task ListWorktreesAsync_Should_Return_The_Typed_Worktrees()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Worktrees);

        var response = await scenario.Client.Worktrees.ListWorktreesAsync(new WorktreeListRequest { ProjectId = "prj_1" });

        await Assert.That(response.Worktrees.Count).IsEqualTo(2);
        await Assert.That(response.Worktrees[0].Directory).IsEqualTo("/repo");
        await Assert.That(response.Worktrees[0].Strategy).IsEqualTo("branch");
        await Assert.That(response.Worktrees[1].Directory).IsEqualTo("/repo-2");
        await Assert.That(response.Worktrees[1].Strategy).IsNull();
        await Assert.That(scenario.Requests.Single().RequestUri)
            .IsEqualTo(new Uri("http://localhost:4096/api/worktree?projectID=prj_1"));
    }

    [Test]
    public async Task ListWorktreesAsync_Should_Return_An_Empty_List()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, "[]");

        var response = await scenario.Client.Worktrees.ListWorktreesAsync(new WorktreeListRequest { ProjectId = "prj_1" });

        await Assert.That(response.Worktrees).IsEmpty();
    }

    [Test]
    public async Task ListWorktreesAsync_Should_Send_The_Project()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.OK, WireBodyData.Worktrees);

        _ = await scenario.Client.Worktrees.ListWorktreesAsync(new WorktreeListRequest
        {
            ProjectId = "prj_1",
        });

        var request = scenario.Requests.Single();
        await Assert.That(request.Method).IsEqualTo(HttpMethod.Get);
        await Assert.That(request.RequestUri!.AbsoluteUri).IsEqualTo(
            "http://localhost:4096/api/worktree?projectID=prj_1");
    }

    [Test]
    public async Task ListWorktreesAsync_Should_Return_The_Declared_Project_Not_Found_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.NotFound, WireBodyData.ProjectNotFoundError);

        var response = await scenario.Client.Worktrees.ListWorktreesAsync(new WorktreeListRequest { ProjectId = "prj_1" }, requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.Status).IsEqualTo(404);
        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Error).IsTypeOf<ProjectNotFoundError>();
        await Assert.That(((ProjectNotFoundError)response.Error!).ProjectId).IsEqualTo("prj_9");
    }

    [Test]
    public async Task ListWorktreesAsync_Should_Throw_The_Declared_400_Error()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.BadRequest, WireBodyData.InvalidRequestError);

        var exception = await Assert
            .That(async () => _ = await scenario.Client.Worktrees.ListWorktreesAsync(new WorktreeListRequest { ProjectId = "prj_1" }))
            .Throws<OpenCodeApiException>();

        await Assert.That(exception!.Status).IsEqualTo(400);
        await Assert.That(exception.Error).IsTypeOf<InvalidRequestError>();
    }

    [Test]
    public async Task ListWorktreesAsync_Should_Return_The_401_Error_On_The_NoThrow_Spine()
    {
        using var scenario = ContractScenario.Responding(HttpStatusCode.Unauthorized, WireBodyData.UnauthorizedError);

        var response = await scenario.Client.Worktrees
            .ListWorktreesAsync(new WorktreeListRequest { ProjectId = "prj_1" }, requestOptions: OpenCodeRequestOptions.NoThrow);

        await Assert.That(response.IsError).IsTrue();
        await Assert.That(response.Status).IsEqualTo(401);
        await Assert.That(response.Error).IsTypeOf<UnauthorizedError>();
    }

    private static WorktreeCreateRequest CreateRequest() =>
        new()
        {
            ProjectId = "prj_1",
            Directory = "/repo/feature",
        };

    private static WorktreeRemoveRequest RemoveRequest() =>
        new()
        {
            ProjectId = "prj_1",
            Directory = "/repo/feature",
            Force = false,
        };
}
