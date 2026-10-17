using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Permissions;

public static class PermissionsDeclaredErrors
{
    private static readonly DeclaredErrorDoor ListSavedDoor = new(
        nameof(PermissionsClient.ListSavedAsync),
        "permission.saved.list",
        "get",
        "/api/permission/saved",
        static async (client, options) => await client.Permissions.ListSavedAsync(requestOptions: options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "permission.saved.list:400:InvalidRequestError",
            ListSavedDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1permission~1saved/get/responses/400/content/application~1json/schema"),
        static () => new(
            "permission.saved.list:401:UnauthorizedError",
            ListSavedDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1permission~1saved/get/responses/401/content/application~1json/schema"),
    ];
}
