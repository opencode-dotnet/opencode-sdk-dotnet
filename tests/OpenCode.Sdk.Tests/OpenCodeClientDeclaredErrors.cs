using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests;

public static class OpenCodeClientDeclaredErrors
{
    private static readonly DeclaredErrorDoor GetLocationDoor = new(
        nameof(OpenCodeClient.GetLocationAsync),
        "location.get",
        "get",
        "/api/location",
        static async (client, options) => await client.GetLocationAsync(requestOptions: options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "location.get:400:InvalidRequestError",
            GetLocationDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1location/get/responses/400/content/application~1json/schema"),
        static () => new(
            "location.get:401:UnauthorizedError",
            GetLocationDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1location/get/responses/401/content/application~1json/schema"),
    ];
}
