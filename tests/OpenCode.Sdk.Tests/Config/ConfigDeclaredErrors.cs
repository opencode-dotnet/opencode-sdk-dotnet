using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Config;

public static class ConfigDeclaredErrors
{
    private static readonly DeclaredErrorDoor GetShellsDoor = new(
        nameof(ConfigClient.GetShellsAsync),
        "config.shells",
        "get",
        "/api/config/shell",
        static async (client, options) => await client.Config.GetShellsAsync(options));

    private static readonly DeclaredErrorDoor UpdateConfigDoor = new(
        nameof(ExperimentalClient.UpdateConfigAsync),
        "experimental.config.update",
        "patch",
        "/api/experimental/config",
        static async (client, options) => await client.Experimental.UpdateConfigAsync(
            new ExperimentalConfigUpdateRequest { Shell = null, }, options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "config.shells:400:InvalidRequestError",
            GetShellsDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1config~1shell/get/responses/400/content/application~1json/schema"),
        static () => new(
            "config.shells:401:UnauthorizedError",
            GetShellsDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1config~1shell/get/responses/401/content/application~1json/schema"),
        static () => new(
            "experimental.config.update:400:InvalidRequestError",
            UpdateConfigDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1experimental~1config/patch/responses/400/content/application~1json/schema"),
        static () => new(
            "experimental.config.update:401:UnauthorizedError",
            UpdateConfigDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1experimental~1config/patch/responses/401/content/application~1json/schema"),
    ];
}
