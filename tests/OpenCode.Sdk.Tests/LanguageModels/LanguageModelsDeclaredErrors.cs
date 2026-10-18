using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.LanguageModels;

public static class LanguageModelsDeclaredErrors
{
    private static readonly DeclaredErrorDoor GetDefaultDoor = new(
        nameof(LanguageModelsClient.GetDefaultAsync),
        "model.default",
        "get",
        "/api/model/default",
        static async (client, options) => await client.LanguageModels.GetDefaultAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor ListModelsDoor = new(
        nameof(LanguageModelsClient.ListModelsAsync),
        "model.list",
        "get",
        "/api/model",
        static async (client, options) => await client.LanguageModels.ListModelsAsync(requestOptions: options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "model.default:400:InvalidRequestError",
            GetDefaultDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1model~1default/get/responses/400/content/application~1json/schema"),
        static () => new(
            "model.default:401:UnauthorizedError",
            GetDefaultDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1model~1default/get/responses/401/content/application~1json/schema"),
        static () => new(
            "model.list:400:InvalidRequestError",
            ListModelsDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1model/get/responses/400/content/application~1json/schema"),
        static () => new(
            "model.list:401:UnauthorizedError",
            ListModelsDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1model/get/responses/401/content/application~1json/schema"),
    ];
}
