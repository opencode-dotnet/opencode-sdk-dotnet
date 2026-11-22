# 🧪 Experimental APIs

Date: 2026-11-22

Upstream serves some of its API under `/api/experimental/`. The SDK exposes it and marks
every member that reaches it with .NET's `[Experimental]` attribute, so the compiler tells you
before you depend on it.

Upstream may change or remove an experimental family in any release, and the SDK follows it: from
`1.0.0`, an experimental family may change in any release that moves the protocol pin.

## Opting in

The first use of an experimental member is a compile **error** that names its diagnostic id:

```text
error OPENCODE001: 'OpenCode.Sdk.ExperimentalClient' is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
```

Suppress the id where you accept it, around the call:

```csharp
#pragma warning disable OPENCODE001 // the experimental wait; upstream may change it
await client.Experimental.WaitForSessionAsync(sessionId, cancellationToken: deadline.Token);
#pragma warning restore OPENCODE001
```

or for the whole project:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);OPENCODE001</NoWarn>
</PropertyGroup>
```

The diagnostic comes from the compiler, not the target framework: every target the packages ship,
`net472` included, reports it under the C# compiler of the .NET 8 SDK (Visual Studio 17.8) or
later. Older compilers ignore the attribute.

## OPENCODE001

The `ExperimentalClient` (`client.Experimental`) and the types only it uses: session stats, import
and export, skills, the session wait, the durable session event stream, instruction entries, text, image,
video and decision generation, automations, background jobs, usage limits, media and media references, blobs, artifacts, well-known integrations, adding,
removing, connecting and disconnecting MCP servers, client-hosted MCP connections and connect tokens,
voice models and sessions, file write and file watching, the v1 migration status, and the config update.
The client-hosted MCP types, extension methods and schema helper carry the same id. The
[streaming guide](streaming.md#-a-single-sessions-log) covers the durable session event stream.

### Media references

Set `MediaReferences = true` when constructing the client to request `media:<hash>` references on
`ListMessagesAsync` (and every `EnumerateMessagesAsync` page), `GetMessageAsync` and `ListInboxAsync`.
The session location's `experimental.media_blob` setting decides whether stored media stays a ref:
such a user file reads as a `PromptAttachment` whose `Kind` is `PromptMediaFileAttachment`, and
`HashFromUri` returns its `Uri`'s 64 lowercase-hex hash, or null for any other value; `GetMediaAsync`
fetches the bytes. For self-contained exports, `InlineMedia = true` omits the automatic capability header:
stored media then comes back inline unless you send that header yourself.

```csharp
#pragma warning disable OPENCODE001 // Accept the experimental media capability.
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

using var client = new OpenCodeClient(new OpenCodeClientOptions
{
    Endpoint = new Uri("http://localhost:4096"),
    MediaReferences = true,
});
var session = client.Sessions.GetSessionClient("ses_example");
var message = await session.GetMessageAsync("msg_example");
if (message.Message is SessionMessageUser { Files: { } files })
{
    foreach (var file in files)
    {
        if (file.Kind == PromptAttachmentKind.PromptMediaFileAttachment
            && MediaReference.HashFromUri(file.PromptMediaFileAttachment.Uri) is { } hash)
        {
            var blob = await client.Experimental.GetMediaAsync(hash);
            Console.WriteLine($"Referenced attachment: {blob.Content.Length} bytes");
        }
    }
}
var export = await session.GetMessageAsync("msg_example",
    requestOptions: new OpenCodeRequestOptions { InlineMedia = true });
#pragma warning restore OPENCODE001
```

### Client-hosted MCP

Your application opens a WebSocket and serves tools over it: your application is the MCP server,
and opencode is the MCP client. This is separate from configuring an MCP server through
`client.McpServers`. First declare the arguments and their source-generated JSON context:

```csharp
using System.Text.Json.Serialization;

public sealed record LookupArguments
{
    [JsonPropertyName("id")]
    public required string Id { get; init; }
}

[JsonSerializable(typeof(LookupArguments))]
internal sealed partial class LookupJsonContext : JsonSerializerContext
{
}
```

Then define a tool and keep the connection alive for as long as the application serves it:

```csharp
using System.Text.Json;
using OpenCode.Sdk;

#pragma warning disable OPENCODE001 // accept the experimental client-hosted MCP contract
var lookup = new ClientMcpTool
{
    Name = "lookup",
    Description = "Look up an item by id",
    InputSchema = ClientMcpSchema.FromType(LookupJsonContext.Default.LookupArguments),
    Handler = (args, ctx) =>
    {
        var input = args.Deserialize(LookupJsonContext.Default.LookupArguments)!;
        return Task.FromResult(new ClientMcpToolResult
        {
            Content = [ClientMcpContent.FromText($"Item {input.Id}, session {ctx.SessionId}")]
        });
    }
};

await using var connection = client.Experimental.ConnectClientMcp(new ClientMcpOptions
{
    Name = "catalog",
    Tools = [lookup]
});
await Task.Delay(Timeout.InfiniteTimeSpan, cancellationToken);
#pragma warning restore OPENCODE001
```

`ConnectClientMcp` returns immediately, not after upgrade or tool registration. There is no public
ready task or connection-state API. The server refuses a client name with characters other than
letters, digits, `_` and `-`, and replaces those characters in a tool name with `_`; the SDK adds no
name validator. A tool is exposed as `<client name>_<tool name>`. Tools retain
declaration order, and the first duplicate name answers a call.

`ClientMcpSchema.FromType` exports an independently owned object-root schema from object or
dictionary metadata, retaining nested nullability; it rejects other roots. It needs no new
dependency or reflection fallback. A manual `InputSchema` is also accepted: the responder starts
with `type: "object"` and then merges your fields, so a supplied `type` overrides that default.
Neither path validates handler arguments. Missing or null arguments become `{}`; other JSON values
arrive unchanged. `ctx.SessionId` is set only when the caller supplied string session metadata.

Return ordered content parts through `ClientMcpToolResult.Content`. `ClientMcpContent.FromText`
creates text; `ClientMcpContent.FromImage(base64Data, mimeType)` passes an image's base64 data and MIME
type without decoding or uploading it. Set `IsError = true` for a tool failure. A handler exception
becomes a text result containing its message with `IsError = true`.

Handlers may overlap. Disconnecting or disposing the connection neither cancels nor waits for them;
your application owns their lifetime and cancellation. The SDK observes their eventual completion,
but never sends a late reply through a replacement socket or replays an executed call. Do not mutate
the supplied collections while connected; manual schemas are cloned at connection admission.
`await using` stops retries and closes only this socket with code 1000, awaiting SDK-owned I/O cleanup,
not application work. The root client does not dispose the connection for you.

Peer closes and ordinary transport failures reconnect after 500 ms, doubling to a 10-second cap.
Only `notifications/initialized` resets that delay, not opening the socket or answering `initialize`.
Initial socket-factory or setup errors throw synchronously. A later retry-factory error is rethrown
from its timer callback, stops retrying and can terminate the process if uncaught.

Without `Url`, the connection uses the root endpoint and merges `Location` over the root's directory.
An explicit `Url` is a base URL: its path and query are retained, and the SDK appends
`api/experimental/client-mcp`, sets `name`, and replaces `location[directory]` only when
`Location.Directory` is supplied. It maps `http`/`ws` to `ws` and `https`/`wss` to `wss`.
Handshake `Headers` override the root Authorization default. A `WebSocketFactory` must return a
fresh, unconnected socket on each attempt; the SDK owns each returned socket.

**Credential destination:** an explicit `Url` receives the root credential unless
an `Authorization` entry in `Headers` overrides it. Select only a destination you intend to trust
with that credential. Client-wide and per-request HTTP header sets do not become handshake headers.

### Browser handoff

Ordinary .NET connections use Authorization headers and need no ticket. For a browser, mint a
short-lived ticket through `client.Experimental.CreateClientMcpConnectTokenAsync`. Supply the
client name and location explicitly; `endpoint` below is the root client's base endpoint:

```csharp
using OpenCode.Sdk;

#pragma warning disable OPENCODE001 // accept the experimental browser connect-token contract
var name = "catalog";
var location = new LocationSelector { Directory = worktree };
var minted = await client.Experimental.CreateClientMcpConnectTokenAsync(
    new ExperimentalMcpClientTokenRequest { Name = name, Location = location },
    cancellationToken: cancellationToken);
var expiresIn = TimeSpan.FromSeconds(minted.ConnectToken.ExpiresIn);
var attachUrl = new UriBuilder(endpoint)
{
    Query = "ticket=" + Uri.EscapeDataString(minted.ConnectToken.Ticket)
}.Uri;
#pragma warning restore OPENCODE001
```

`minted.ConnectToken.Ticket` is the credential, `ExpiresIn` is its lifetime in seconds (60 by
default), and `minted.Location` is the resolved location. Keep the ticket out of logs. Use
`attachUrl` as `ClientMcpOptions.Url` with the same `Name`, `Location` and your tools. For a browser's
native WebSocket, append `api/experimental/client-mcp` beneath that base path, map to `ws`/`wss`,
and include `name`, `location[directory]` and `ticket` in the final upgrade query.

The ticket is single-use and scoped to the name and resolved directory/workspace on the server that
minted it. Token creation always uses the root HTTP endpoint and credentials; it does not follow
the attachment `Url`. A ticket minted on server A does not authorize an attachment to server B.
The token door supplies its required request-header sentinel internally; there is no public header
argument. Attach and reconnect never mint or refresh tickets automatically: a consumed or expired
ticket cannot authorize a later retry. The explicit-`Url` credential warning above still applies
to a .NET attachment using a ticket.

## OPENCODE002

The persistent-PTY family (`client.PersistentPtys`): the collection and handle clients, the
WebSocket connect door with its session, options and frames, and the types only it uses.
`OpenCodeServer.ShutdownPersistentPtyAsync`, which discovers the background service and shuts its
persistent terminals down, carries the same id. The
[terminals guide](terminals.md#-persistent-ptys) covers how it works.
