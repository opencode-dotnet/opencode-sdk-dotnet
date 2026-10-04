using System.Text.Json;
using OpenCode.Sdk.Models;

namespace OpenCode.Sdk.Sandbox;

/// <summary>
/// The B-1 mechanism leg of the standing walkthrough: the bodyless POSTs, the PUT family
/// (mcp add, pty update, the instructions entry), and the batch's new error types carried
/// live over the NoThrow spine.
/// </summary>
internal static class MechanismActionsWalkthrough
{
    public static async Task RunAsync(OpenCodeClient client, SessionClient handle, string sessionId)
    {
        ArgumentNullException.ThrowIfNull(client);
        ArgumentNullException.ThrowIfNull(handle);

        var interrupt = await handle.InterruptAsync().ConfigureAwait(false);

        Console.WriteLine($"interrupt: status={interrupt.Status} isError={interrupt.IsError}");

        var revertClear = await handle.ClearRevertAsync(OpenCodeRequestOptions.NoThrow).ConfigureAwait(false);

        Console.WriteLine(revertClear.IsError
            ? $"revert-clear: status={revertClear.Status} error={ErrorName(revertClear)}"
            : $"revert-clear: status={revertClear.Status}");

        using var value = JsonDocument.Parse("\"answer tersely\"");
        var entryPut = await client.Experimental.SetSessionInstructionsEntryAsync(sessionId,
                "style",
                new ExperimentalSessionInstructionsEntryRequest { Value = value.RootElement })
            .ConfigureAwait(false);

        Console.WriteLine($"instructions-put: status={entryPut.Status}");

        var entryRemove = await client.Experimental.RemoveSessionInstructionsEntryAsync(sessionId, "style", OpenCodeRequestOptions.NoThrow).ConfigureAwait(false);

        Console.WriteLine($"instructions-remove: status={entryRemove.Status} isError={entryRemove.IsError}");

        var formCancel = await handle.CancelFormAsync("frm_missing", requestOptions: OpenCodeRequestOptions.NoThrow).ConfigureAwait(false);

        Console.WriteLine(formCancel.IsError
            ? $"form-cancel: status={formCancel.Status} error={ErrorName(formCancel)}"
            : $"form-cancel: status={formCancel.Status}");

        var mcpAdd = await client.Experimental.AddMcpServerAsync("sandbox-echo",
                new ExperimentalMcpAddRequest
                {
                    Config = new McpLocalConfig { Command = ["bun", "--version"], Disabled = true },
                },
                OpenCodeRequestOptions.NoThrow)
            .ConfigureAwait(false);

        Console.WriteLine(mcpAdd.IsError
            ? $"mcp-add: status={mcpAdd.Status} error={ErrorName(mcpAdd)}"
            : $"mcp-add: status={mcpAdd.Status}");

        var mcpDisconnect = await client.Experimental.DisconnectMcpServerAsync("sandbox-echo", null, OpenCodeRequestOptions.NoThrow).ConfigureAwait(false);

        Console.WriteLine(mcpDisconnect.IsError
            ? $"mcp-disconnect: status={mcpDisconnect.Status} error={ErrorName(mcpDisconnect)}"
            : $"mcp-disconnect: status={mcpDisconnect.Status}");

        var mcpRemove = await client.Experimental.RemoveMcpServerAsync("sandbox-echo", null, OpenCodeRequestOptions.NoThrow).ConfigureAwait(false);

        Console.WriteLine($"mcp-remove: status={mcpRemove.Status} isError={mcpRemove.IsError}");

        var pty = await client.Ptys.CreatePtyAsync(new PtyCreateRequest
        {
            Command = "pwsh",
            Title = "sdk mechanism demo",
        }).ConfigureAwait(false);

        Console.WriteLine($"pty-create: status={pty.Status} id={pty.Pty.Id} title={pty.Pty.Title}");

        var ptyHandle = client.Ptys.GetPtyClient(pty.Pty.Id);
        var ptyUpdate = await ptyHandle.UpdateAsync(new PtyUpdateRequest
        {
            Title = "renamed by PUT",
        }).ConfigureAwait(false);

        Console.WriteLine($"pty-update: status={ptyUpdate.Status} title={ptyUpdate.Pty.Title}");

        var ptyRemove = await ptyHandle.RemoveAsync(null, OpenCodeRequestOptions.NoThrow).ConfigureAwait(false);

        Console.WriteLine($"pty-remove: status={ptyRemove.Status} isError={ptyRemove.IsError}");
    }

    private static string ErrorName(OpenCodeResponse response) => response.Error?.GetType().Name ?? "<untyped>";
}
