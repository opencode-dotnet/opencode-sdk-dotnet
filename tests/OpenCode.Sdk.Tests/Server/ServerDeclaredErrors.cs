using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Server;

public static class ServerDeclaredErrors
{
    private static readonly DeclaredErrorDoor GetInfoDoor = new(
        nameof(ServerClient.GetInfoAsync),
        "server.info",
        "get",
        "/api/info",
        static async (client, options) => await client.Server.GetInfoAsync(options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "server.info:400:InvalidRequestError",
            GetInfoDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1info/get/responses/400/content/application~1json/schema"),
        static () => new(
            "server.info:401:UnauthorizedError",
            GetInfoDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1info/get/responses/401/content/application~1json/schema"),
    ];
}
