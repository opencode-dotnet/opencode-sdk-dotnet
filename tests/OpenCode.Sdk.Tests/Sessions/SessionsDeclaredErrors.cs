using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Sessions;

public static class SessionsDeclaredErrors
{
    private static readonly DeclaredErrorDoor GetDoor = new(
        nameof(SessionClient.GetAsync),
        "session.get",
        "get",
        "/api/session/{sessionID}",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_9").GetAsync(options));

    private static readonly DeclaredErrorDoor GetPermissionDoor = new(
        nameof(SessionClient.GetPermissionAsync),
        "session.permission.get",
        "get",
        "/api/session/{sessionID}/permission/{requestID}",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_100").GetPermissionAsync("req_9", options));

    private static readonly DeclaredErrorDoor ListMessagesDoor = new(
        nameof(SessionClient.ListMessagesAsync),
        "session.message.list",
        "get",
        "/api/session/{sessionID}/message",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_9").ListMessagesAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor GetDiffFromMsg9Door = new(
        nameof(SessionClient.GetDiffAsync),
        "session.diff",
        "get",
        "/api/session/{sessionID}/diff",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_100")
            .GetDiffAsync(new SessionDiffRequest { From = "msg_9" }, options));

    private static readonly DeclaredErrorDoor GetDiffDefaultDoor = new(
        nameof(SessionClient.GetDiffAsync),
        "session.diff",
        "get",
        "/api/session/{sessionID}/diff",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_100").GetDiffAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor GetContextSes9Door = new(
        nameof(SessionClient.GetContextAsync),
        "session.context",
        "get",
        "/api/session/{sessionID}/context",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_9").GetContextAsync(options));

    private static readonly DeclaredErrorDoor GetContextSes100Door = new(
        nameof(SessionClient.GetContextAsync),
        "session.context",
        "get",
        "/api/session/{sessionID}/context",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_100").GetContextAsync(options));

    private static readonly DeclaredErrorDoor ListFormsSes9Door = new(
        nameof(SessionClient.ListFormsAsync),
        "session.form.list",
        "get",
        "/api/session/{sessionID}/form",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_9").ListFormsAsync(options));

    private static readonly DeclaredErrorDoor ListFormsSes100Door = new(
        nameof(SessionClient.ListFormsAsync),
        "session.form.list",
        "get",
        "/api/session/{sessionID}/form",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_100").ListFormsAsync(options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "session.get:404:SessionNotFoundError",
            GetDoor,
            HttpStatusCode.NotFound,
            WireBodyData.SessionNotFoundError,
            "SessionNotFoundError",
            typeof(SessionNotFoundError),
            "#/paths/~1api~1session~1{sessionID}/get/responses/404/content/application~1json/schema"),
        static () => new(
            "session.get:401:UnauthorizedError",
            GetDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1session~1{sessionID}/get/responses/401/content/application~1json/schema"),
        static () => new(
            "session.permission.get:404:PermissionNotFoundError",
            GetPermissionDoor,
            HttpStatusCode.NotFound,
            WireBodyData.PermissionNotFoundError,
            "PermissionNotFoundError",
            typeof(PermissionNotFoundError),
            "#/paths/~1api~1session~1{sessionID}~1permission~1{requestID}/get/responses/404/content/application~1json/schema/anyOf/1"),
        static () => new(
            "session.message.list:404:SessionNotFoundError",
            ListMessagesDoor,
            HttpStatusCode.NotFound,
            WireBodyData.SessionNotFoundError,
            "SessionNotFoundError",
            typeof(SessionNotFoundError),
            "#/paths/~1api~1session~1{sessionID}~1message/get/responses/404/content/application~1json/schema"),
        static () => new(
            "session.diff:404:MessageNotFoundError",
            GetDiffFromMsg9Door,
            HttpStatusCode.NotFound,
            WireBodyData.MessageNotFoundError,
            "MessageNotFoundError",
            typeof(MessageNotFoundError),
            "#/paths/~1api~1session~1{sessionID}~1diff/get/responses/404/content/application~1json/schema/anyOf/0"),
    ];

    public static IEnumerable<Func<DeclaredErrorCase>> Rows2() =>
    [
        static () => new(
            "session.diff:400:InvalidRequestError",
            GetDiffDefaultDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1session~1{sessionID}~1diff/get/responses/400/content/application~1json/schema"),
        static () => new(
            "session.context:404:SessionNotFoundError",
            GetContextSes9Door,
            HttpStatusCode.NotFound,
            WireBodyData.SessionNotFoundError,
            "SessionNotFoundError",
            typeof(SessionNotFoundError),
            "#/paths/~1api~1session~1{sessionID}~1context/get/responses/404/content/application~1json/schema"),
        static () => new(
            "session.context:401:UnauthorizedError",
            GetContextSes100Door,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1session~1{sessionID}~1context/get/responses/401/content/application~1json/schema"),
        static () => new(
            "session.form.list:404:SessionNotFoundError",
            ListFormsSes9Door,
            HttpStatusCode.NotFound,
            WireBodyData.SessionNotFoundError,
            "SessionNotFoundError",
            typeof(SessionNotFoundError),
            "#/paths/~1api~1session~1{sessionID}~1form/get/responses/404/content/application~1json/schema/anyOf/0"),
        static () => new(
            "session.form.list:401:UnauthorizedError",
            ListFormsSes100Door,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1session~1{sessionID}~1form/get/responses/401/content/application~1json/schema"),
    ];
}
