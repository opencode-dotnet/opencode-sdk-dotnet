using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Sandbox;

/// <summary>
/// The session-breadth leg of the standing walkthrough: export with its query, the permission
/// round trip, forking the current session, and the NoThrow spine carrying whatever the live
/// server declares for compact.
/// </summary>
internal static class SessionActionsWalkthrough
{
    public static async Task RunAsync(OpenCodeClient client, SessionClient handle, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(handle);

        var export = await client.Experimental.GetSessionExportAsync(sessionId, new ExperimentalSessionExportRequest { Sanitize = true }).ConfigureAwait(false);

        Console.WriteLine($"export:  status={export.Status} id={export.SessionExport.Info.Id} messages={export.SessionExport.Messages.Count}");

        var permission = await handle.CreatePermissionAsync(new SessionPermissionCreateRequest
        {
            Action = "read",
            Resources = ["file:///sandbox/demo.txt"],
        }).ConfigureAwait(false);

        Console.WriteLine($"permission-create: status={permission.Status} id={permission.Permission.Id} effect={permission.Permission.Effect}");

        var permissionGet = await handle.GetPermissionAsync(permission.Permission.Id, OpenCodeRequestOptions.NoThrow).ConfigureAwait(false);

        Console.WriteLine(permissionGet.IsError
            ? $"permission-get: status={permissionGet.Status} error={ErrorName(permissionGet)}"
            : $"permission-get: status={permissionGet.Status} action={permissionGet.Permission.Action}");

        var reply = await handle.ReplyToPermissionAsync(
                permission.Permission.Id,
                new SessionPermissionReplyRequest { Decision = PermissionReply.Once },
                OpenCodeRequestOptions.NoThrow)
            .ConfigureAwait(false);

        Console.WriteLine($"permission-reply: status={reply.Status} isError={reply.IsError}");

        var compact = await handle.CompactAsync(null, OpenCodeRequestOptions.NoThrow).ConfigureAwait(false);

        Console.WriteLine(compact.IsError
            ? $"compact: status={compact.Status} error={ErrorName(compact)}"
            : $"compact: status={compact.Status} inbox={compact.Compact.Id} delivery={compact.Compact.Delivery}");

        var fork = await handle.ForkAsync(
                new SessionForkRequest(),
                OpenCodeRequestOptions.NoThrow)
            .ConfigureAwait(false);

        Console.WriteLine(fork.IsError
            ? $"fork:    status={fork.Status} error={ErrorName(fork)}"
            : $"fork:    status={fork.Status} id={fork.Fork.Id}");
    }

    private static string ErrorName(OpenCodeResponse response) => response.Error?.GetType().Name ?? "<untyped>";
}
