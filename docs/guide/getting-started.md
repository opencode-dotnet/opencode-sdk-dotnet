# 🚀 Getting started

Date: 2026-10-16

Install the package, point a client at a server, and make three calls. Ten minutes, and the last
one talks to a model.

- [📦 Install](#-install)
- [🔌 Construct a client](#-construct-a-client)
- [▶️ Your first calls](#️-your-first-calls)
- [🧭 How the client is organised](#-how-the-client-is-organised)
- [➡️ Where to go next](#️-where-to-go-next)

## 📦 Install

```bash
dotnet add package OpenCodeDotNet.Sdk --prerelease
dotnet add package OpenCodeDotNet.Sdk.Extensions --prerelease   # dependency injection, optional
```

> **The package id is not the namespace.** You install `OpenCodeDotNet.Sdk` and you write
> `using OpenCode.Sdk;` — nuget.org reserves the `OpenCode.` id prefix for an unrelated owner, so
> the artifact carries a different name than the code inside it.

The packages target `netstandard2.0`, `net472`, `net10.0`, and `net11.0`, so any project on one of
those works — a .NET Framework project after one language setting, below. A project on an older
.NET, such as `net8.0` or `net9.0`, still installs the `netstandard2.0` asset but gets a build
warning: that runtime is not one the packages are tested on. Nightly builds
of `master` live on a GitHub Packages feed; the source command and its `read:packages` token
requirement live in one place so they never drift:
[**Installation** in the root README](../../README.md#-installation).

You also need an `opencode` server. Either install the CLI and run one yourself, or let the SDK
start a private one for you — [connection modes](connection-modes.md) covers both, and
[**Installation** in the root README](../../README.md#-installation) has the CLI install line.

### .NET Framework projects

`dotnet new console` offers no `net472` framework, so create the project with its default framework
and retarget it. A .NET Framework project also defaults to C# 7.3, which is too old for the SDK:
its public surface uses async streams and `init` accessors, which need C# 9, and the console
template's implicit usings need C# 10. Set both properties in the project file:

```xml
<TargetFramework>net472</TargetFramework>
<LangVersion>10.0</LangVersion>
```

Nothing else is needed — no extra package and no polyfill; the packages' `net472` dependencies
arrive with them. A project without implicit usings can use `9.0`.

## 🔌 Construct a client

`OpenCodeClient` is the root. It owns its transport, so construct it once and keep it — a singleton
per server is the intended shape, not a per-call object.

```csharp
using OpenCode.Sdk;
using OpenCode.Sdk.Models;
```

```csharp
using var client = new OpenCodeClient(new OpenCodeClientOptions
{
    Endpoint = new Uri("http://127.0.0.1:4096"),
    Password = Environment.GetEnvironmentVariable("OPENCODE_PASSWORD"),
});
```

`OpenCodeClientOptions` has four members, and only the first is normally yours to think about:

| Member | Type | Meaning |
|---|---|---|
| `Endpoint` | `Uri?` | The server's base address. Required. |
| `Password` | `string?` | The HTTP Basic password. Required for any server the `opencode` CLI started — it always runs with one, generated and printed as `server password <pw>` when you set none. `null` sends no credential at all, which only a server embedded without authentication accepts; any other value is sent exactly as written. |
| `Username` | `string` | The Basic username. Defaults to `opencode` — the only username the pinned server accepts — so leave it alone unless upstream changes. |
| `Location` | `LocationSelector?` | The ambient directory header values, overridable per call; only operations that resolve location from those headers use them. |

Session creation selects its location from `SessionCreateRequest.Location`; leaving it unset uses
the server's working directory. Session listing filters with `SessionListRequest.Directory` or
`Project`, independently of these headers. See the [location contract](../architecture/client-runtime.md#location)
for ambient and per-call header behavior.

> **🔑 The client reads no environment variables of its own.** `OPENCODE_PASSWORD` above is
> *your* code reading *your* environment — exactly how opencode's own CLI layers it. Options are
> snapshotted at construction, so changing the environment later never reaches a live client. The
> one door that does read the environment is `OpenCodeServer.DiscoverAsync`, which locates the
> CLI's background-service registration through the same four path variables the CLI uses — see
> [discovering the background service](connection-modes.md#️-discovering-the-background-service).

Options are validated when the client is built: a missing endpoint or a blank or colon-bearing
username throws straight away rather than on the first call.

## ▶️ Your first calls

### Is the server alive?

```csharp
var info = await client.Server.GetInfoAsync();

Console.WriteLine($"opencode {info.ServerInfo.Version} (pid {info.ServerInfo.Pid})");
```

`GetLocationAsync` is the only operation directly on the root client. Status lives on `Server`.

### Create a session and send a prompt

A **session** is a durable conversation. Create one, take a handle bound to its id, and post to it:

```csharp
var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest { Title = "hello from .NET" });
var session = client.Sessions.GetSessionClient(created.Session.Id);

var prompt = await session.PromptAsync(new SessionPromptRequest { Text = "Summarize this repository." });

Console.WriteLine($"queued {prompt.Prompt.Id} in session {created.Session.Id}");
```

`PromptAsync` **queues** the turn and returns the inbox entry it created — the assistant's
answer arrives asynchronously, which is what [streaming](streaming.md) is for. If you just want
text back from a model with no conversation state, use generate instead:

```csharp
var generated = await session.GenerateTextAsync(new SessionGenerateRequest { Prompt = "Name three C# testing libraries." });

Console.WriteLine(generated.Generate.Text);
```

To keep the conversation and still get the answer in a console app, listen on the event bus,
prompt, and read until the session's turn ends; then read its messages. Open the bus first: an
event published before you listen is gone.

```csharp
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
await using var events = client.Events.SubscribeAsync(deadline.Token).GetAsyncEnumerator();

// The first event is server.connected: from here on, nothing the session publishes is missed.
await events.MoveNextAsync();

await session.PromptAsync(new SessionPromptRequest { Text = "Summarize this repository." }, cancellationToken: deadline.Token);

while (await events.MoveNextAsync())
{
    if (events.Current is SessionExecutionSucceeded or SessionExecutionFailed or SessionExecutionInterrupted
        && events.Current is ISessionScopedEvent { SessionId: var id } && id == created.Session.Id)
    {
        break;
    }
}

await foreach (var message in session.EnumerateMessagesAsync(new SessionMessageListRequest { Order = ListOrder.Ascending }, deadline.Token))
{
    if (message is SessionMessageAssistant assistant)
    {
        foreach (var text in assistant.Content.OfType<SessionMessageAssistantText>())
        {
            Console.WriteLine(text.Text);
        }
    }
}
```

The server publishes exactly one of the three ending events each time the session goes from
working to idle, and the deadline is yours to set. [Streaming](streaming.md#-waiting-for-a-turn-to-end)
explains the order and why `SessionIdle` is not the event to wait for.

`client.Experimental.WaitForSessionAsync(created.Session.Id)` waits for the same moment in one
call, without the bus. Upstream serves it as an experimental route, so it needs an
[opt-in](experimental.md#opencode001) and may still move.

### Choosing a model

The calls above let the server pick a model. To pick one yourself, read the current catalog:

```csharp
var providers = await client.Providers.ListProvidersAsync();
var models = await client.LanguageModels.ListModelsAsync();
Console.WriteLine($"{providers.Providers.Count} providers, {models.Models.Count} models");

var model = models.Models.FirstOrDefault(candidate => candidate.Enabled);
if (model is not null)
{
    var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
    {
        Title = "picked a model",
        Model = new ModelRef { ProviderId = model.ProviderId, Id = model.Id },
    });
    Console.WriteLine($"session {created.Session.Id} on {model.ProviderId}/{model.Id}");
}
```

- **`ModelRef.Id` takes `ModelInfo.Id`, never `ModelInfo.ModelId`.** The former is the catalog
  identity; the latter is the provider's own model id. They can differ for an aliased model.
- **Catalogs are observations during asynchronous plugin activation.** A successful status call
  does not make them complete, and the server exposes no activation barrier: when your application
  expects a particular provider or model, wait for that identity under a caller-owned cancellation
  deadline (`CONTEXT.md`, Plugin activation).
- **Three calls wait for the location's plugins; take that wait once, at startup.**
  `ListModelsAsync`, `GetDefaultAsync` and `client.Experimental.GenerateTextAsync` wait up to five
  seconds for the location's plugins to load. On the first request to a location, after a config
  or plugin change, or on the server's periodic plugin refresh, they can answer `503` with a
  `ServiceUnavailableError`, which means "plugins still loading". Every other call is unaffected.
  Make one readiness step before anything model-related, as opencode's own CLI does: ask for the
  default model, and on that answer wait briefly and ask again, under a deadline. After that, use
  the client normally; if a later config or plugin change brings the answer back for a moment, the
  same step handles it there too.

  ```csharp
  using var startup = new CancellationTokenSource(TimeSpan.FromSeconds(30));
  while (true)
  {
      try
      {
          await client.LanguageModels.GetDefaultAsync(cancellationToken: startup.Token);
          break;
      }
      catch (OpenCodeApiException failure) when (failure.Error is ServiceUnavailableError)
      {
          await Task.Delay(TimeSpan.FromMilliseconds(250), startup.Token);
      }
  }
  ```
- **The VCS summary settles the same way, per location.** `client.Vcs.GetVcsAsync()` for a
  directory the server has not served before can answer before that location's git plugin has
  reported: `Vcs.Provider` is `null` and both branch values are `null`, which means *not known
  yet*, not "no branch". Wait for a non-null `Provider` under a caller-owned deadline. Once
  `Provider` is set, a `null` `Branch.Current` means a detached HEAD. Observed on
  `@opencode/cli@2.0.15`.
- **Neither create nor prompt validates the ref.** A wrong provider/catalog-id pair surfaces later
  as a failed turn rather than a typed model error at the call.

The catalog calls above resolve their location the same way the rest of the client does: from the
client's ambient `Location`, or the server's own working directory when you set none. Each request
type carries its own `Location` if you want to read one location's catalog from a client pointed at
another. Creating a session is different: its directory comes from the request body, so
`SessionCreateRequest.Location` is a `LocationPublicRef`, not a `LocationSelector`. See
[per-call location](requests.md#-per-call-location).

And **provider inventory is the machine's ambient opencode configuration**, not something the SDK
or the launcher supplies. A host with no provider credentials configured lists nothing, and
`OpenCodeServer.StartAsync` does not change that — it starts a fresh process, not a fresh machine.
If you have no preference at all, `client.LanguageModels.GetDefaultAsync()` answers with the
location's default model, whose `Default` payload is legitimately `null` on such a host.

### Export a session transcript

`Experimental.GetSessionExportAsync` hands back the session plus every settled message in one `SessionTransferData`,
and `Experimental.ImportSessionAsync` takes that same shape back at a location (answering 409 for an id that
already exists):

```csharp
var export = await client.Experimental.GetSessionExportAsync(created.Session.Id, new ExperimentalSessionExportRequest { Sanitize = true });

Console.WriteLine($"{export.SessionExport.Messages.Count} messages from {export.SessionExport.Info.Id}");
```

> **✂️ `Sanitize` redacts by design.** With `Sanitize = true` the server rewrites
> message text to placeholders before it answers — `[redacted:text:<messageId>]` for user text,
> `[redacted:synthetic:<messageId>]` for synthetic text, `[redacted:session-title:<sessionId>]` for
> the title, and the same treatment for the directory. Ids, types, order, and count all survive
> untouched. So a sanitized export is for sharing the *shape* of a conversation, never for
> comparing transcripts: compare ids, not text. Leave `Sanitize` unset or `false` when you want the
> conversation itself. Verified on `@opencode/cli@2.0.2`.

`Experimental` methods take route identifiers explicitly, including session IDs. Import uses
`ExperimentalSessionImportRequest`.

## 🧭 How the client is organised

The root client exposes **27 families** as properties — `Sessions`, `Events`, `Ptys`,
`PersistentPtys`, `Shells`, `Providers`, `LanguageModels`, `Agents`, `Skills`, `Commands`,
`Permissions`, `Credentials`, `Config`, `Projects`, `Worktrees`, `Vcs`, `FileSystem`,
`Forms`, `Integrations`, `McpServers`, `Plugins`, `References`, `Rpc`, `Server`,
`Websearch`, `Debug`, and `Experimental`:

```csharp
var providers = await client.Providers.ListProvidersAsync();
var agents = await client.Agents.ListAgentsAsync();

Console.WriteLine($"{providers.Providers.Count} providers, {agents.Agents.Count} agents");
```

Two shapes repeat everywhere, and once you have seen them the rest of the surface reads itself:

- **Collection client → bound handle.** Where the API keys operations by an id, the collection
  client has a `Get*Client(id)` factory and the handle carries the id-keyed operations:
  `client.Sessions.GetSessionClient(id)`, `client.Ptys.GetPtyClient(id)`,
  `client.PersistentPtys.GetPersistentPtyClient(id)`. The handle is a cheap value — take one per
  id, keep it as long as you like.
- **Three optional tails.** Almost every operation is
  `…Async(request, requestOptions, cancellationToken)`, where `request` carries the body and query
  and is optional when every member is, `requestOptions` selects per-call behaviour such as
  [`NoThrow`](errors-and-responses.md#-ask-for-the-failure-as-data-instead), and the token is the
  usual one. Streaming operations are the exception — they take no `requestOptions`.
  [Requests](requests.md) covers what goes in that first argument, including the members that tell
  "leave this alone" apart from "clear this".

## ➡️ Where to go next

| If you want to… | Read |
|---|---|
| Let the SDK start its own server, or wire the client into a `Host` | [Connection modes](connection-modes.md) |
| Shape a request body, clear a server-side value, or scope one call to a directory | [Requests](requests.md) |
| React to what the server is doing, live | [Streaming](streaming.md) |
| Drive a real terminal | [Terminals](terminals.md) |
| Branch on a failure instead of catching it | [Errors and responses](errors-and-responses.md) |
| Walk a long message or session history | [Pagination](pagination.md) |

For a full runnable program that exercises most of the surface against a live server, the
in-repo sandbox is committed and documented:
[`tests/OpenCode.Sdk.Sandbox`](../../tests/OpenCode.Sdk.Sandbox/README.md).
