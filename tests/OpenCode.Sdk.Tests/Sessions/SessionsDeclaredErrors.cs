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

    private static readonly DeclaredErrorDoor ListInboxSes9Door = new(
        nameof(SessionClient.ListInboxAsync),
        "session.inbox.list",
        "get",
        "/api/session/{sessionID}/inbox",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_9").ListInboxAsync(options));

    private static readonly DeclaredErrorDoor ListInboxSes100Door = new(
        nameof(SessionClient.ListInboxAsync),
        "session.inbox.list",
        "get",
        "/api/session/{sessionID}/inbox",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_100").ListInboxAsync(options));

    private static readonly DeclaredErrorDoor ListSessionInstructionsEntriesSes9Door = new(
        nameof(ExperimentalClient.ListSessionInstructionsEntriesAsync),
        "experimental.session.instructions.entry.list",
        "get",
        "/api/experimental/session/{sessionID}/instructions/entries",
        static async (client, options) => await client.Experimental.ListSessionInstructionsEntriesAsync("ses_9", options));

    private static readonly DeclaredErrorDoor ListSessionInstructionsEntriesSes100Door = new(
        nameof(ExperimentalClient.ListSessionInstructionsEntriesAsync),
        "experimental.session.instructions.entry.list",
        "get",
        "/api/experimental/session/{sessionID}/instructions/entries",
        static async (client, options) => await client.Experimental.ListSessionInstructionsEntriesAsync("ses_100", options));

    private static readonly DeclaredErrorDoor ListRequestsSes9Door = new(
        nameof(SessionClient.ListRequestsAsync),
        "session.permission.list",
        "get",
        "/api/session/{sessionID}/permission",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_9").ListRequestsAsync(options));

    private static readonly DeclaredErrorDoor ListRequestsSes100Door = new(
        nameof(SessionClient.ListRequestsAsync),
        "session.permission.list",
        "get",
        "/api/session/{sessionID}/permission",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_100").ListRequestsAsync(options));

    private static readonly DeclaredErrorDoor SetEnvironmentSes9Door = new(
        nameof(SessionClient.SetEnvironmentAsync),
        "session.environment",
        "put",
        "/api/session/{sessionID}/environment",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_9").SetEnvironmentAsync(
            new SessionEnvironmentRequest { Variables = new Dictionary<string, string>(StringComparer.Ordinal), }, options));

    private static readonly DeclaredErrorDoor SetEnvironmentSes100Door = new(
        nameof(SessionClient.SetEnvironmentAsync),
        "session.environment",
        "put",
        "/api/session/{sessionID}/environment",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_100").SetEnvironmentAsync(
            new SessionEnvironmentRequest { Variables = new Dictionary<string, string>(StringComparer.Ordinal), }, options));

    private static readonly DeclaredErrorDoor CreateFormDoor = new(
        nameof(SessionClient.CreateFormAsync),
        "session.form.create",
        "post",
        "/api/session/{sessionID}/form",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_100").CreateFormAsync(
            new SessionFormCreateRequest { Title = "Pick a name", Fields = [], }, options));

    private static readonly DeclaredErrorDoor GetFormFrm9Door = new(
        nameof(SessionClient.GetFormAsync),
        "session.form.get",
        "get",
        "/api/session/{sessionID}/form/{formID}",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_100").GetFormAsync("frm_9", options));

    private static readonly DeclaredErrorDoor GetFormFrm1Door = new(
        nameof(SessionClient.GetFormAsync),
        "session.form.get",
        "get",
        "/api/session/{sessionID}/form/{formID}",
        static async (client, options) => await client.Sessions.GetSessionClient("ses_100").GetFormAsync("frm_1", options));

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

    public static IEnumerable<Func<DeclaredErrorCase>> Rows3() =>
    [
        static () => new(
            "session.inbox.list:404:SessionNotFoundError",
            ListInboxSes9Door,
            HttpStatusCode.NotFound,
            WireBodyData.SessionNotFoundError,
            "SessionNotFoundError",
            typeof(SessionNotFoundError),
            "#/paths/~1api~1session~1{sessionID}~1inbox/get/responses/404/content/application~1json/schema"),
        static () => new(
            "session.inbox.list:401:UnauthorizedError",
            ListInboxSes100Door,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1session~1{sessionID}~1inbox/get/responses/401/content/application~1json/schema"),
        static () => new(
            "experimental.session.instructions.entry.list:404:SessionNotFoundError",
            ListSessionInstructionsEntriesSes9Door,
            HttpStatusCode.NotFound,
            WireBodyData.SessionNotFoundError,
            "SessionNotFoundError",
            typeof(SessionNotFoundError),
            "#/paths/~1api~1experimental~1session~1{sessionID}~1instructions~1entries/get/responses/404/content/application~1json/schema/anyOf/0"),
        static () => new(
            "experimental.session.instructions.entry.list:401:UnauthorizedError",
            ListSessionInstructionsEntriesSes100Door,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1experimental~1session~1{sessionID}~1instructions~1entries/get/responses/401/content/application~1json/schema"),
        static () => new(
            "session.permission.list:404:SessionNotFoundError",
            ListRequestsSes9Door,
            HttpStatusCode.NotFound,
            WireBodyData.SessionNotFoundError,
            "SessionNotFoundError",
            typeof(SessionNotFoundError),
            "#/paths/~1api~1session~1{sessionID}~1permission/get/responses/404/content/application~1json/schema/anyOf/0"),
        static () => new(
            "session.permission.list:401:UnauthorizedError",
            ListRequestsSes100Door,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1session~1{sessionID}~1permission/get/responses/401/content/application~1json/schema"),
    ];

    public static IEnumerable<Func<DeclaredErrorCase>> Rows4() =>
    [
        static () => new(
            "session.environment:404:SessionNotFoundError",
            SetEnvironmentSes9Door,
            HttpStatusCode.NotFound,
            WireBodyData.SessionNotFoundError,
            "SessionNotFoundError",
            typeof(SessionNotFoundError),
            "#/paths/~1api~1session~1{sessionID}~1environment/put/responses/404/content/application~1json/schema"),
        static () => new(
            "session.environment:401:UnauthorizedError",
            SetEnvironmentSes100Door,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1session~1{sessionID}~1environment/put/responses/401/content/application~1json/schema"),
        static () => new(
            "session.form.create:409:ConflictError",
            CreateFormDoor,
            HttpStatusCode.Conflict,
            WireBodyData.ConflictError,
            "ConflictError",
            typeof(ConflictError),
            "#/paths/~1api~1session~1{sessionID}~1form/post/responses/409/content/application~1json/schema"),
        static () => new(
            "session.form.create:401:UnauthorizedError",
            CreateFormDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1session~1{sessionID}~1form/post/responses/401/content/application~1json/schema"),
        static () => new(
            "session.form.get:404:FormNotFoundError",
            GetFormFrm9Door,
            HttpStatusCode.NotFound,
            "{\"_tag\":\"FormNotFoundError\",\"id\":\"frm_9\",\"message\":\"gone\"}",
            "FormNotFoundError",
            typeof(FormNotFoundError),
            "#/paths/~1api~1session~1{sessionID}~1form~1{formID}/get/responses/404/content/application~1json/schema/anyOf/1"),
        static () => new(
            "session.form.get:401:UnauthorizedError",
            GetFormFrm1Door,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1session~1{sessionID}~1form~1{formID}/get/responses/401/content/application~1json/schema"),
    ];
}
