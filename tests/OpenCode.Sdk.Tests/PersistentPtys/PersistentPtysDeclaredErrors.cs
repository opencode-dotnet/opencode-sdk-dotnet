using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.PersistentPtys;

public static class PersistentPtysDeclaredErrors
{
    private static readonly DeclaredErrorDoor ListPersistentPtysDoor = new(
        nameof(PersistentPtysClient.ListPersistentPtysAsync),
        "server.experimental.persistentPty.list",
        "get",
        "/api/experimental/session/{sessionID}/terminal",
        static async (client, options) => await client.PersistentPtys.ListPersistentPtysAsync("ses_1", requestOptions: options));

    private static readonly DeclaredErrorDoor ReadLinesZeroDoor = new(
        nameof(PersistentPtysClient.ReadAsync),
        "server.experimental.persistentPty.read",
        "get",
        "/api/experimental/session/{sessionID}/terminal/read",
        static async (client, options) => await client.PersistentPtys.ReadAsync("ses_1", new PersistentPtyReadRequest { Lines = 0 }, options));

    private static readonly DeclaredErrorDoor HandoffDoor = new(
        nameof(PersistentPtysClient.HandoffAsync),
        "server.experimental.persistentPty.handoff",
        "post",
        "/api/experimental/persistent-pty/handoff",
        static async (client, options) => await client.PersistentPtys.HandoffAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor ShutdownDoor = new(
        nameof(PersistentPtysClient.ShutdownAsync),
        "server.experimental.persistentPty.shutdown",
        "post",
        "/api/experimental/persistent-pty/shutdown",
        static async (client, options) => await client.PersistentPtys.ShutdownAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor GetDoor = new(
        nameof(PersistentPtyClient.GetAsync),
        "server.experimental.persistentPty.get",
        "get",
        "/api/experimental/persistent-pty/{ptyID}",
        static async (client, options) => await client.PersistentPtys.GetPersistentPtyClient("pty_persistent_7").GetAsync(options));

    private static readonly DeclaredErrorDoor UpdateDoor = new(
        nameof(PersistentPtyClient.UpdateAsync),
        "server.experimental.persistentPty.update",
        "put",
        "/api/experimental/persistent-pty/{ptyID}",
        static async (client, options) => await client.PersistentPtys.GetPersistentPtyClient("pty_persistent_7")
            .UpdateAsync(new PersistentPtyUpdateRequest { Size = new PtySize { Cols = 120, Rows = 40 } }, options));

    private static readonly DeclaredErrorDoor RemoveDoor = new(
        nameof(PersistentPtyClient.RemoveAsync),
        "server.experimental.persistentPty.remove",
        "delete",
        "/api/experimental/persistent-pty/{ptyID}",
        static async (client, options) => await client.PersistentPtys.GetPersistentPtyClient("pty_persistent_7").RemoveAsync(options));

    private static readonly DeclaredErrorDoor GetSnapshotDoor = new(
        nameof(PersistentPtyClient.GetSnapshotAsync),
        "server.experimental.persistentPty.snapshot",
        "get",
        "/api/experimental/persistent-pty/{ptyID}/snapshot",
        static async (client, options) => await client.PersistentPtys.GetPersistentPtyClient("pty_persistent_7").GetSnapshotAsync(options));

    private static readonly DeclaredErrorDoor CreateConnectTokenDoor = new(
        nameof(PersistentPtyClient.CreateConnectTokenAsync),
        "server.experimental.persistentPty.connectToken",
        "post",
        "/api/experimental/persistent-pty/{ptyID}/connect-token",
        static async (client, options) => await client.PersistentPtys.GetPersistentPtyClient("pty_persistent_7").CreateConnectTokenAsync(options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "server.experimental.persistentPty.list:503:ServiceUnavailableError",
            ListPersistentPtysDoor,
            HttpStatusCode.ServiceUnavailable,
            WireBodyData.ServiceUnavailableError,
            "ServiceUnavailableError",
            typeof(ServiceUnavailableError),
            "#/paths/~1api~1experimental~1session~1{sessionID}~1terminal/get/responses/503/content/application~1json/schema"),
        static () => new(
            "server.experimental.persistentPty.read:400:InvalidRequestError:lines-zero",
            ReadLinesZeroDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1experimental~1session~1{sessionID}~1terminal~1read/get/responses/400/content/application~1json/schema"),
        static () => new(
            "server.experimental.persistentPty.handoff:503:ServiceUnavailableError",
            HandoffDoor,
            HttpStatusCode.ServiceUnavailable,
            WireBodyData.ServiceUnavailableError,
            "ServiceUnavailableError",
            typeof(ServiceUnavailableError),
            "#/paths/~1api~1experimental~1persistent-pty~1handoff/post/responses/503/content/application~1json/schema"),
        static () => new(
            "server.experimental.persistentPty.shutdown:503:ServiceUnavailableError",
            ShutdownDoor,
            HttpStatusCode.ServiceUnavailable,
            WireBodyData.ServiceUnavailableError,
            "ServiceUnavailableError",
            typeof(ServiceUnavailableError),
            "#/paths/~1api~1experimental~1persistent-pty~1shutdown/post/responses/503/content/application~1json/schema"),
        static () => new(
            "server.experimental.persistentPty.get:404:PtyNotFoundError",
            GetDoor,
            HttpStatusCode.NotFound,
            WireBodyData.PtyNotFoundError,
            "PtyNotFoundError",
            typeof(PtyNotFoundError),
            "#/paths/~1api~1experimental~1persistent-pty~1{ptyID}/get/responses/404/content/application~1json/schema"),
        static () => new(
            "server.experimental.persistentPty.update:404:PtyNotFoundError",
            UpdateDoor,
            HttpStatusCode.NotFound,
            WireBodyData.PtyNotFoundError,
            "PtyNotFoundError",
            typeof(PtyNotFoundError),
            "#/paths/~1api~1experimental~1persistent-pty~1{ptyID}/put/responses/404/content/application~1json/schema"),
        static () => new(
            "server.experimental.persistentPty.remove:404:PtyNotFoundError",
            RemoveDoor,
            HttpStatusCode.NotFound,
            WireBodyData.PtyNotFoundError,
            "PtyNotFoundError",
            typeof(PtyNotFoundError),
            "#/paths/~1api~1experimental~1persistent-pty~1{ptyID}/delete/responses/404/content/application~1json/schema"),
        static () => new(
            "server.experimental.persistentPty.snapshot:404:PtyNotFoundError",
            GetSnapshotDoor,
            HttpStatusCode.NotFound,
            WireBodyData.PtyNotFoundError,
            "PtyNotFoundError",
            typeof(PtyNotFoundError),
            "#/paths/~1api~1experimental~1persistent-pty~1{ptyID}~1snapshot/get/responses/404/content/application~1json/schema"),
    ];

    public static IEnumerable<Func<DeclaredErrorCase>> Rows2() =>
    [
        static () => new(
            "server.experimental.persistentPty.connectToken:403:ForbiddenError",
            CreateConnectTokenDoor,
            HttpStatusCode.Forbidden,
            WireBodyData.ForbiddenError,
            "ForbiddenError",
            typeof(ForbiddenError),
            "#/paths/~1api~1experimental~1persistent-pty~1{ptyID}~1connect-token/post/responses/403/content/application~1json/schema"),
        static () => new(
            "server.experimental.persistentPty.connectToken:404:PtyNotFoundError",
            CreateConnectTokenDoor,
            HttpStatusCode.NotFound,
            WireBodyData.PtyNotFoundError,
            "PtyNotFoundError",
            typeof(PtyNotFoundError),
            "#/paths/~1api~1experimental~1persistent-pty~1{ptyID}~1connect-token/post/responses/404/content/application~1json/schema"),
    ];
}
