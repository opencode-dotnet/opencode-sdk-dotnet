using System.Net;
using OpenCode.Sdk.Models;
using OpenCode.Sdk.Tests.Common.Support;
using OpenCode.Sdk.Tests.Support;

namespace OpenCode.Sdk.Tests.Worktrees;

public static class WorktreesDeclaredErrors
{
    private static readonly DeclaredErrorDoor CreateWorktreeDoor = new(
        nameof(WorktreesClient.CreateWorktreeAsync),
        "worktree.create",
        "post",
        "/api/worktree",
        static async (client, options) => await client.Worktrees.CreateWorktreeAsync(
            new WorktreeCreateRequest { ProjectId = "prj_1", Directory = "/repo/feature" }, options));

    private static readonly DeclaredErrorDoor RemoveWorktreeDoor = new(
        nameof(WorktreesClient.RemoveWorktreeAsync),
        "worktree.remove",
        "delete",
        "/api/worktree",
        static async (client, options) => await client.Worktrees.RemoveWorktreeAsync(
            new WorktreeRemoveRequest { ProjectId = "prj_1", Directory = "/repo/feature", Force = false }, options));

    private static readonly DeclaredErrorDoor RefreshWorktreesDoor = new(
        nameof(WorktreesClient.RefreshWorktreesAsync),
        "worktree.refresh",
        "post",
        "/api/worktree/refresh",
        static async (client, options) => await client.Worktrees.RefreshWorktreesAsync(new WorktreeRefreshRequest { ProjectId = "prj_1" }, options));

    private static readonly DeclaredErrorDoor ListWorktreesDoor = new(
        nameof(WorktreesClient.ListWorktreesAsync),
        "worktree.list",
        "get",
        "/api/worktree",
        static async (client, options) => await client.Worktrees.ListWorktreesAsync(new WorktreeListRequest { ProjectId = "prj_1" }, options));

    public static IEnumerable<Func<DeclaredErrorCase>> Rows() =>
    [
        static () => new(
            "worktree.create:400:InvalidRequestError",
            CreateWorktreeDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1worktree/post/responses/400/content/application~1json/schema/anyOf/1"),
        static () => new(
            "worktree.create:401:UnauthorizedError",
            CreateWorktreeDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1worktree/post/responses/401/content/application~1json/schema"),
        static () => new(
            "worktree.remove:401:UnauthorizedError",
            RemoveWorktreeDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1worktree/delete/responses/401/content/application~1json/schema"),
        static () => new(
            "worktree.refresh:400:WorktreeError",
            RefreshWorktreesDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.WorktreeError,
            "WorktreeError",
            typeof(WorktreeError),
            "#/paths/~1api~1worktree~1refresh/post/responses/400/content/application~1json/schema/anyOf/0"),
        static () => new(
            "worktree.refresh:401:UnauthorizedError",
            RefreshWorktreesDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1worktree~1refresh/post/responses/401/content/application~1json/schema"),
        static () => new(
            "worktree.list:400:InvalidRequestError",
            ListWorktreesDoor,
            HttpStatusCode.BadRequest,
            WireBodyData.InvalidRequestError,
            "InvalidRequestError",
            typeof(InvalidRequestError),
            "#/paths/~1api~1worktree/get/responses/400/content/application~1json/schema"),
        static () => new(
            "worktree.list:401:UnauthorizedError",
            ListWorktreesDoor,
            HttpStatusCode.Unauthorized,
            WireBodyData.UnauthorizedError,
            "UnauthorizedError",
            typeof(UnauthorizedError),
            "#/paths/~1api~1worktree/get/responses/401/content/application~1json/schema"),
    ];
}
