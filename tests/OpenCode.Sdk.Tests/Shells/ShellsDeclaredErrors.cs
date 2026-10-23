using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Shells;

public static class ShellsDeclaredErrors
{
    private static readonly DeclaredErrorDoor GetOutputSh9Door = new(
        nameof(ShellClient.GetOutputAsync),
        "shell.output",
        "get",
        "/api/shell/{id}/output",
        static async (client, options) => await client.Shells.GetShellClient("sh_9").GetOutputAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor GetOutputSh100Door = new(
        nameof(ShellClient.GetOutputAsync),
        "shell.output",
        "get",
        "/api/shell/{id}/output",
        static async (client, options) => await client.Shells.GetShellClient("sh_100").GetOutputAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor GetDoor = new(
        nameof(ShellClient.GetAsync),
        "shell.get",
        "get",
        "/api/shell/{id}",
        static async (client, options) => await client.Shells.GetShellClient("sh_9").GetAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor CreateShellDoor = new(
        nameof(ShellsClient.CreateShellAsync),
        "shell.create",
        "post",
        "/api/shell",
        static async (client, options) => await client.Shells.CreateShellAsync(new ShellCreateRequest { Command = "ls", Timeout = 1 }, options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "shell.output:404:ShellNotFoundError",
            GetOutputSh9Door,
            HttpStatusCode.NotFound,
            WireBodyData.ShellNotFoundError,
            "ShellNotFoundError",
            typeof(ShellNotFoundError),
            "#/paths/~1api~1shell~1{id}~1output/get/responses/404/content/application~1json/schema/anyOf/0"),
        static () => new(
            "shell.output:401:UnauthorizedError",
            GetOutputSh100Door,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1shell~1{id}~1output/get/responses/401/content/application~1json/schema"),
        static () => new(
            "shell.get:404:ShellNotFoundError",
            GetDoor,
            HttpStatusCode.NotFound,
            WireBodyData.ShellNotFoundError,
            "ShellNotFoundError",
            typeof(ShellNotFoundError),
            "#/paths/~1api~1shell~1{id}/get/responses/404/content/application~1json/schema/anyOf/0"),
        static () => new(
            "shell.create:400:InvalidRequestError",
            CreateShellDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1shell/post/responses/400/content/application~1json/schema"),
    ];
}
