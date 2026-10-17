using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Experimental;

public static class ExperimentalDeclaredErrors
{
    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "experimental.fs.write:400:InvalidRequestError",
            new DeclaredErrorDoor(
                nameof(ExperimentalClient.WriteFileAsync),
                "experimental.fs.write",
                "post",
                "/api/experimental/fs/write",
                static async (client, options) =>
                {
                    using var content = new MemoryStream([0x01]);
                    return await client.Experimental.WriteFileAsync(
                        new ExperimentalFsWriteRequest { Path = "a.bin" }, content, options);
                }),
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1experimental~1fs~1write/post/responses/400/content/application~1json/schema"),
    ];
}
