using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Projects;

public static class ProjectsDeclaredErrors
{
    private static readonly DeclaredErrorDoor ListProjectsDoor = new(
        nameof(ProjectsClient.ListProjectsAsync),
        "project.list",
        "get",
        "/api/project",
        static async (client, options) => await client.Projects.ListProjectsAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor UpdateProjectPrj9Door = new(
        nameof(ProjectsClient.UpdateProjectAsync),
        "project.update",
        "patch",
        "/api/project/{projectID}",
        static async (client, options) => await client.Projects.UpdateProjectAsync("prj_9", requestOptions: options));

    private static readonly DeclaredErrorDoor UpdateProjectPrj1Door = new(
        nameof(ProjectsClient.UpdateProjectAsync),
        "project.update",
        "patch",
        "/api/project/{projectID}",
        static async (client, options) => await client.Projects.UpdateProjectAsync("prj_1", requestOptions: options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "project.list:400:InvalidRequestError",
            ListProjectsDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1project/get/responses/400/content/application~1json/schema"),
        static () => new(
            "project.list:401:UnauthorizedError",
            ListProjectsDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1project/get/responses/401/content/application~1json/schema"),
        static () => new(
            "project.update:404:ProjectNotFoundError",
            UpdateProjectPrj9Door,
            HttpStatusCode.NotFound,
            WireBodyData.ProjectNotFoundError,
            "ProjectNotFoundError",
            typeof(ProjectNotFoundError),
            "#/paths/~1api~1project~1{projectID}/patch/responses/404/content/application~1json/schema/anyOf/0"),
        static () => new(
            "project.update:401:UnauthorizedError",
            UpdateProjectPrj1Door,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1project~1{projectID}/patch/responses/401/content/application~1json/schema"),
    ];
}
