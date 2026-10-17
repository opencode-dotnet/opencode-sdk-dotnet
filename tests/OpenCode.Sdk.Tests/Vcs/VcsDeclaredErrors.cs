using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Vcs;

public static class VcsDeclaredErrors
{
    private static readonly DeclaredErrorDoor GetStatusDoor = new(
        nameof(VcsClient.GetStatusAsync),
        "vcs.status",
        "get",
        "/api/vcs/status",
        static async (client, options) => await client.Vcs.GetStatusAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor ListBranchesDoor = new(
        nameof(VcsClient.ListBranchesAsync),
        "vcs.branch.list",
        "get",
        "/api/vcs/branch",
        static async (client, options) => await client.Vcs.ListBranchesAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor GetBaseDoor = new(
        nameof(VcsClient.GetBaseAsync),
        "vcs.base",
        "get",
        "/api/vcs/base",
        static async (client, options) => await client.Vcs.GetBaseAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor GetDiffBranchDoor = new(
        nameof(VcsClient.GetDiffAsync),
        "vcs.diff",
        "get",
        "/api/vcs/diff",
        static async (client, options) => await client.Vcs.GetDiffAsync(new VcsDiffRequest { Mode = VcsMode.Branch }, options));

    private static readonly DeclaredErrorDoor GetDiffWorkingDoor = new(
        nameof(VcsClient.GetDiffAsync),
        "vcs.diff",
        "get",
        "/api/vcs/diff",
        static async (client, options) => await client.Vcs.GetDiffAsync(new VcsDiffRequest { Mode = VcsMode.Working }, options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "vcs.status:400:InvalidRequestError",
            GetStatusDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1vcs~1status/get/responses/400/content/application~1json/schema"),
        static () => new(
            "vcs.status:401:UnauthorizedError",
            GetStatusDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1vcs~1status/get/responses/401/content/application~1json/schema"),
        static () => new(
            "vcs.branch.list:400:InvalidRequestError",
            ListBranchesDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1vcs~1branch/get/responses/400/content/application~1json/schema"),
        static () => new(
            "vcs.branch.list:401:UnauthorizedError",
            ListBranchesDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1vcs~1branch/get/responses/401/content/application~1json/schema"),
        static () => new(
            "vcs.base:503:ServiceUnavailableError",
            GetBaseDoor,
            HttpStatusCode.ServiceUnavailable,
            WireBodyData.ServiceUnavailableError,
            "ServiceUnavailableError",
            typeof(ServiceUnavailableError),
            "#/paths/~1api~1vcs~1base/get/responses/503/content/application~1json/schema"),
        static () => new(
            "vcs.base:401:UnauthorizedError",
            GetBaseDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1vcs~1base/get/responses/401/content/application~1json/schema"),
        static () => new(
            "vcs.diff:503:ServiceUnavailableError",
            GetDiffBranchDoor,
            HttpStatusCode.ServiceUnavailable,
            WireBodyData.ServiceUnavailableError,
            "ServiceUnavailableError",
            typeof(ServiceUnavailableError),
            "#/paths/~1api~1vcs~1diff/get/responses/503/content/application~1json/schema"),
        static () => new(
            "vcs.diff:401:UnauthorizedError",
            GetDiffWorkingDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1vcs~1diff/get/responses/401/content/application~1json/schema"),
    ];
}
