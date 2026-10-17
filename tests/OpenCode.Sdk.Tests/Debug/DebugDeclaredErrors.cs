using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Debug;

public static class DebugDeclaredErrors
{
    private static readonly DeclaredErrorDoor ListLocationsDoor = new(
        nameof(DebugClient.ListLocationsAsync),
        "debug.location.list",
        "get",
        "/api/debug/location",
        static async (client, options) => await client.Debug.ListLocationsAsync(requestOptions: options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "debug.location.list:400:InvalidRequestError",
            ListLocationsDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1debug~1location/get/responses/400/content/application~1json/schema"),
        static () => new(
            "debug.location.list:401:UnauthorizedError",
            ListLocationsDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1debug~1location/get/responses/401/content/application~1json/schema"),
    ];
}
