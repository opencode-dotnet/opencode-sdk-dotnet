using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Ptys;

public static class PtysDeclaredErrors
{
    private static readonly DeclaredErrorDoor CreateConnectTokenDoor = new(
        nameof(PtyClient.CreateConnectTokenAsync),
        "pty.connect.token",
        "post",
        "/api/pty/{ptyID}/connect-token",
        static async (client, options) => await client.Ptys.GetPtyClient("pty_9").CreateConnectTokenAsync(requestOptions: options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "pty.connect.token:404:PtyNotFoundError",
            CreateConnectTokenDoor,
            HttpStatusCode.NotFound,
            WireBodyData.PtyNotFoundError,
            "PtyNotFoundError",
            typeof(PtyNotFoundError),
            "#/paths/~1api~1pty~1{ptyID}~1connect-token/post/responses/404/content/application~1json/schema/anyOf/0"),
        static () => new(
            "pty.connect.token:403:ForbiddenError",
            CreateConnectTokenDoor,
            HttpStatusCode.Forbidden,
            WireBodyData.ForbiddenError,
            "ForbiddenError",
            typeof(ForbiddenError),
            "#/paths/~1api~1pty~1{ptyID}~1connect-token/post/responses/403/content/application~1json/schema"),
    ];
}
