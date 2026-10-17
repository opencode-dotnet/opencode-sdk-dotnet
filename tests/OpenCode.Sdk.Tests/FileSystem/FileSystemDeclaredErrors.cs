using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.FileSystem;

public static class FileSystemDeclaredErrors
{
    private static readonly DeclaredErrorDoor ListEntriesDoor = new(
        nameof(FileSystemClient.ListEntriesAsync),
        "fs.list",
        "get",
        "/api/fs/list",
        static async (client, options) => await client.FileSystem.ListEntriesAsync(requestOptions: options));

    private static readonly DeclaredErrorDoor FindEntriesDoor = new(
        nameof(FileSystemClient.FindEntriesAsync),
        "fs.find",
        "get",
        "/api/fs/find",
        static async (client, options) => await client.FileSystem.FindEntriesAsync(new FsFindRequest { Query = "todo" }, options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "fs.list:400:InvalidRequestError",
            ListEntriesDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1fs~1list/get/responses/400/content/application~1json/schema"),
        static () => new(
            "fs.list:401:UnauthorizedError",
            ListEntriesDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1fs~1list/get/responses/401/content/application~1json/schema"),
        static () => new(
            "fs.find:400:InvalidRequestError",
            FindEntriesDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1fs~1find/get/responses/400/content/application~1json/schema"),
        static () => new(
            "fs.find:401:UnauthorizedError",
            FindEntriesDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1fs~1find/get/responses/401/content/application~1json/schema"),
    ];
}
