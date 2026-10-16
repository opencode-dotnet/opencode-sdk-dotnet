using System.Globalization;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Tests.Sessions.Support;

internal sealed class PendingPermissionCleanup(SessionClient session)
{
    public async Task RejectAsync(string? permissionId, CancellationToken cancellationToken)
    {
        if (permissionId is null)
        {
            return;
        }

        var response = await session.ReplyToPermissionAsync(
            permissionId,
            new SessionPermissionReplyRequest { Decision = PermissionReply.Reject },
            OpenCodeRequestOptions.NoThrow,
            cancellationToken);
        if (response is { Status: 204, IsError: false }
            || (response is { Status: 404, IsError: true, Error: PermissionNotFoundError missing }
                && missing.RequestId == permissionId))
        {
            return;
        }

        throw new OpenCodeApiException(
            "Pending permission rejection failed: status=" + response.Status.ToString(CultureInfo.InvariantCulture)
            + "; error=" + (response.Error?.GetType().Name ?? "<no typed error>") + ".",
            response.Status, response.Error, response.RawBody);
    }
}
