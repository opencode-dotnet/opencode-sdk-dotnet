using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Credentials;

public static class CredentialsDeclaredErrors
{
    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "credential.create:409:ConflictError",
            new DeclaredErrorDoor(
                nameof(CredentialsClient.CreateCredentialAsync),
                "credential.create",
                "post",
                "/api/credential",
                static async (client, options) => await client.Credentials.CreateCredentialAsync(
                    new CredentialCreateRequest
                    {
                        Id = "cred_1",
                        IntegrationId = "openai",
                        Value = new CredentialKey { Key = "sk-test" },
                    }, options)),
            HttpStatusCode.Conflict,
            WireBodyData.ConflictError,
            "ConflictError",
            typeof(ConflictError),
            "#/paths/~1api~1credential/post/responses/409/content/application~1json/schema"),
        static () => new(
            "credential.list:401:UnauthorizedError",
            new DeclaredErrorDoor(
                nameof(CredentialsClient.ListCredentialsAsync),
                "credential.list",
                "get",
                "/api/credential",
                static async (client, options) => await client.Credentials.ListCredentialsAsync(options)),
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1credential/get/responses/401/content/application~1json/schema"),
        static () => new(
            "credential.activate:400:InvalidRequestError",
            new DeclaredErrorDoor(
                nameof(CredentialsClient.ActivateCredentialAsync),
                "credential.activate",
                "post",
                "/api/credential/{credentialID}/activate",
                static async (client, options) => await client.Credentials.ActivateCredentialAsync("cred_1", options)),
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1credential~1{credentialID}~1activate/post/responses/400/content/application~1json/schema"),
        static () => new(
            "credential.activate:401:UnauthorizedError",
            new DeclaredErrorDoor(
                nameof(CredentialsClient.ActivateCredentialAsync),
                "credential.activate",
                "post",
                "/api/credential/{credentialID}/activate",
                static async (client, options) => await client.Credentials.ActivateCredentialAsync("cred_1", options)),
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1credential~1{credentialID}~1activate/post/responses/401/content/application~1json/schema"),
    ];
}
