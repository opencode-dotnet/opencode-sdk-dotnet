using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Forms;

public static class FormsDeclaredErrors
{
    private static readonly DeclaredErrorDoor ListFormsDoor = new(
        nameof(FormsClient.ListFormsAsync),
        "form.list",
        "get",
        "/api/form",
        static async (client, options) => await client.Forms.ListFormsAsync(requestOptions: options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "form.list:400:InvalidRequestError",
            ListFormsDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1form/get/responses/400/content/application~1json/schema"),
        static () => new(
            "form.list:401:UnauthorizedError",
            ListFormsDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1form/get/responses/401/content/application~1json/schema"),
    ];
}
