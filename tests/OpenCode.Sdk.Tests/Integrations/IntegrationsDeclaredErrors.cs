using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Integrations;

public static class IntegrationsDeclaredErrors
{
    private static readonly DeclaredErrorDoor GetDoor = new(
        nameof(IntegrationClient.GetAsync),
        "integration.get",
        "get",
        "/api/integration/{integrationID}",
        static async (client, options) => await client.Integrations.GetIntegrationClient("int_1").GetAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor ConnectWithKeyDoor = new(
        nameof(IntegrationClient.ConnectWithKeyAsync),
        "integration.connect.key",
        "post",
        "/api/integration/{integrationID}/connect/key",
        static async (client, options) => await client.Integrations.GetIntegrationClient("int_1").ConnectWithKeyAsync(
            new IntegrationConnectKeyRequest { Key = "sk-test" }, options));

    private static readonly DeclaredErrorDoor BeginOAuthConnectionDoor = new(
        nameof(IntegrationClient.BeginOAuthConnectionAsync),
        "integration.oauth.connect",
        "post",
        "/api/integration/{integrationID}/connect/oauth",
        static async (client, options) => await client.Integrations.GetIntegrationClient("int_1").BeginOAuthConnectionAsync(
            new IntegrationOAuthConnectRequest { MethodId = "github" }, options));

    private static readonly DeclaredErrorDoor ListIntegrationsDoor = new(
        nameof(IntegrationsClient.ListIntegrationsAsync),
        "integration.list",
        "get",
        "/api/integration",
        static async (client, options) => await client.Integrations.ListIntegrationsAsync(requestOptions: options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "integration.get:400:InvalidRequestError",
            GetDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1integration~1{integrationID}/get/responses/400/content/application~1json/schema"),
        static () => new(
            "integration.get:401:UnauthorizedError",
            GetDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1integration~1{integrationID}/get/responses/401/content/application~1json/schema"),
        static () => new(
            "integration.connect.key:400:InvalidRequestError",
            ConnectWithKeyDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1integration~1{integrationID}~1connect~1key/post/responses/400/content/application~1json/schema"),
        static () => new(
            "integration.connect.key:401:UnauthorizedError",
            ConnectWithKeyDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1integration~1{integrationID}~1connect~1key/post/responses/401/content/application~1json/schema"),
        static () => new(
            "integration.oauth.connect:400:InvalidRequestError",
            BeginOAuthConnectionDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1integration~1{integrationID}~1connect~1oauth/post/responses/400/content/application~1json/schema"),
        static () => new(
            "integration.oauth.connect:401:UnauthorizedError",
            BeginOAuthConnectionDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1integration~1{integrationID}~1connect~1oauth/post/responses/401/content/application~1json/schema"),
        static () => new(
            "integration.list:400:InvalidRequestError",
            ListIntegrationsDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1integration/get/responses/400/content/application~1json/schema"),
        static () => new(
            "integration.list:401:UnauthorizedError",
            ListIntegrationsDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1integration/get/responses/401/content/application~1json/schema"),
    ];
}
