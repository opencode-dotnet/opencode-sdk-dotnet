using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Worktrees;

public static class WorktreesDeclaredErrors
{
    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "worktree.create:400:InvalidRequestError",
            new DeclaredErrorDoor(
                nameof(WorktreesClient.CreateWorktreeAsync),
                "worktree.create",
                "post",
                "/api/worktree",
                static async (client, options) => await client.Worktrees.CreateWorktreeAsync(
                    new WorktreeCreateRequest { ProjectId = "prj_1", Directory = "/repo/feature" }, options)),
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1worktree/post/responses/400/content/application~1json/schema/anyOf/1"),
    ];
}
