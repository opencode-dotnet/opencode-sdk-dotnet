# opencode SDK for .NET

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/LICENSE) [![NuGet](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Fpackages%2Fnuget%2FOpenCodeDotNet.Sdk)](https://www.nuget.org/packages/OpenCodeDotNet.Sdk) [![CI](https://github.com/opencode-dotnet/opencode-sdk-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/opencode-dotnet/opencode-sdk-dotnet/actions/workflows/ci.yml) [![Linux Tests](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Ftests%2Flinux%2Fopencode-dotnet%2Fopencode-sdk-dotnet%2Fmaster)](https://api.localstackfor.net/redirect/test-results/linux/opencode-dotnet/opencode-sdk-dotnet/master)

> **🚀 Quick Start**: [Install](#-installation) | [Quick start](#-quick-start) | [Playground](#-playground) | [Guide](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/getting-started.md) | [API coverage](#-api-coverage)

> **Unofficial.** This project is not affiliated with or endorsed by the
> [opencode](https://opencode.ai) team.

A strongly typed .NET client for the [opencode](https://github.com/anomalyco/opencode) server — the
HTTP API that every opencode front-end (TUI, desktop, web UI, plugins) uses. The SDK speaks the
OpenCode 2.x API.

```csharp
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

await using var server = await OpenCodeServer.StartAsync();
using var client = server.CreateClient();

var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest { Title = "hello from .NET" });
Console.WriteLine($"session {created.Session.Id}");
```

> **Prefer learning by example?** [QuickStart](https://github.com/opencode-dotnet/opencode-sdk-dotnet/tree/master/playground/quickstart) starts a private server and streams a model's reply, no API key needed. The [playground](#-playground) has three more apps.

---

## 💡 Why this SDK?

- **Typed all the way down.** Every operation has a generated request type, a generated response
  envelope, and typed error models. That includes opencode's unions without a discriminator, which
  off-the-shelf .NET OpenAPI generators did not represent correctly.
- **One transport for every connection mode.** The same pipeline owns endpoint authority,
  authentication, buffering, and failure mapping, whether you start the server, point at one, or
  discover the CLI's background service.
- **Errors you can branch on.** A call throws a typed exception by default. With
  `OpenCodeRequestOptions.NoThrow`, it returns the failure as data instead — useful when a 404 is a
  normal answer.
- **Broad .NET reach.** `net472` is a first-class target, so the SDK works in .NET Framework
  hosts, not only in modern apps; `netstandard2.0` is a compatibility asset for other runtimes.
- **No reflection serialization.** `System.Text.Json` source generation throughout. Both packages
  declare `IsAotCompatible` on every modern target.
- **A pinned protocol.** Each refresh to a new upstream release comes with a receipt, so a
  regeneration is a diff that you can review.

## 🧭 API Coverage

Every operation in the pinned OpenAPI snapshot is callable except the three declined below; the
two terminal connections go through hand-written WebSocket transports. All three connection modes
work: a private server the SDK starts, a server you already run, and the background service that
the opencode CLI registers.

`OpenCodeClient` exposes one sub-client per area of the API. Every sub-client is also injectable on
its own when you use dependency injection.

The HTTP API has no file-content search: `FileSystemClient.FindEntriesAsync` ranks file and directory paths by fuzzy match; it does not search contents.

| Area | Entry point | Guide |
|---|---|---|
| Sessions, messages, prompts | `client.Sessions` | [Requests](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/requests.md), [Pagination](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/pagination.md) |
| Durable session event streams | `client.Experimental.ReadSessionLogAsync` | [Streaming](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md) |
| The live event stream | `client.Events` | [Streaming](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md) |
| Terminals and persistent terminals | `client.Ptys`, `client.PersistentPtys` | [Terminals](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/terminals.md) |
| Shell commands | `client.Shells` | [Requests](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/requests.md) |
| Files and version control | `client.FileSystem`, `client.Vcs`, `client.Worktrees` | [Requests](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/requests.md) |
| Permissions and forms | `client.Permissions`, `client.Forms` | [Requests](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/requests.md) |
| Models and providers | `client.Providers`, `client.LanguageModels`, `client.Credentials` | — |
| Agents, commands, skills, plugins | `client.Agents`, `client.Commands`, `client.Skills`, `client.Plugins` | — |
| MCP servers and integrations | `client.McpServers`, `client.Integrations` | — |
| Configuration, projects, references | `client.Config`, `client.Projects`, `client.References` | — |
| Server info and pairing | `client.Server` | [Connection modes](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/connection-modes.md) |
| Locations | `client.GetLocationAsync`, `client.ReloadLocationsAsync`, `client.Debug` | [Requests](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/requests.md) |
| Web search and RPC | `client.Websearch`, `client.Rpc` | — |
| Operations upstream marks experimental | `client.Experimental` | [Experimental APIs](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md) |

The two terminal connections (`pty.connect`, `experimental.persistentPty.connect`) are WebSocket
upgrades that the HTTP pipeline cannot carry. They are fully usable through the hand-written
`PtySession` and `PersistentPtySession` types. Three are declined by decision: the client-hosted MCP
attach and its token, deferred to a hand-written API in 1.1 ([#529](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/529)), and the legacy text generation
endpoint, which upstream's own clients omit.
[`src/OpenCode.Sdk/.generation-incomplete`](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/src/OpenCode.Sdk/.generation-incomplete)
is the machine-readable map that the build reads.

## 📦 Installation

```bash
dotnet add package OpenCodeDotNet.Sdk
dotnet add package OpenCodeDotNet.Sdk.Extensions   # dependency injection, optional
```

> **The package id is not the namespace.** You install `OpenCodeDotNet.Sdk`, and you write
> `using OpenCode.Sdk;`. nuget.org reserves the `OpenCode.` id prefix for an unrelated owner, so
> only the `PackageReference` carries the `OpenCodeDotNet` name.

You also need the `opencode` CLI, from the `@opencode/cli` npm scope. Install the release that this
repository pins — later releases usually work, but they are not what the tests run against:

```sh
npm install -g @opencode/cli@2.2.6
```

You do not have to start it. `OpenCodeServer.StartAsync()` in the [quick start](#-quick-start)
starts a private server for you.

### Nightly builds (GitHub Packages)

Every code push to `master` publishes a nightly of the next version, `{version}-nightly.{yyyyMMdd}.g{shortSha}`, to GitHub Packages:

```bash
# Add the GitHub Packages source (PAT: classic token with the read:packages scope)
dotnet nuget add source https://nuget.pkg.github.com/opencode-dotnet/index.json \
  --name github-opencode-sdk \
  --username YOUR_GITHUB_USERNAME \
  --password YOUR_GITHUB_PAT \
  --store-password-in-clear-text

# Install the nightly packages
dotnet add package OpenCodeDotNet.Sdk --prerelease --source github-opencode-sdk
dotnet add package OpenCodeDotNet.Sdk.Extensions --prerelease --source github-opencode-sdk
```

To keep the token out of shell history, put the source and its credentials in a `nuget.config` instead:

```xml
<?xml version="1.0" encoding="utf-8"?>
<configuration>
  <packageSources>
    <add key="nuget.org" value="https://api.nuget.org/v3/index.json" />
    <add key="github-opencode-sdk" value="https://nuget.pkg.github.com/opencode-dotnet/index.json" />
  </packageSources>
  <packageSourceCredentials>
    <github-opencode-sdk>
      <add key="Username" value="%GITHUB_USERNAME%" />
      <add key="ClearTextPassword" value="%GITHUB_PAT%" />
    </github-opencode-sdk>
  </packageSourceCredentials>
</configuration>
```

> **🔑 GitHub Packages authentication**: GitHub Packages requires a
> [classic Personal Access Token](https://docs.github.com/en/authentication/keeping-your-account-and-data-secure/creating-a-personal-access-token)
> with the `read:packages` scope, even for public packages. The NuGet registry does not accept
> fine-grained tokens. Linux and macOS need `--store-password-in-clear-text`, because NuGet cannot
> encrypt stored credentials there. Never commit a real token. Inside GitHub Actions you need no
> PAT: the workflow's own `GITHUB_TOKEN` works as the password.

## 🚀 Quick Start

Pick the connection mode that matches where your server lives:

| You want… | Use | Who stops the server |
|---|---|---|
| A private server for this app only | `OpenCodeServer.StartAsync()` | The SDK, when you dispose it |
| A server you already run | `new OpenCodeClient(options)` | You |
| The background service the CLI shares with other clients | `OpenCodeServer.DiscoverAsync()` or `EnsureAsync()` | Nobody, unless you call `StopAsync()` |

### The SDK starts the server

The launcher starts a private `opencode serve` child, creates its password, and gives you a client
bound to it. It resolves `opencode` from `PATH` as a shell does, `PATHEXT` included, so the npm
`.cmd` shim works on Windows.

```csharp
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

await using var server = await OpenCodeServer.StartAsync();
using var client = server.CreateClient();

var info = await client.Server.GetInfoAsync();
Console.WriteLine($"opencode {info.ServerInfo.Version} (pid {info.ServerInfo.Pid})");

using var window = new CancellationTokenSource(TimeSpan.FromSeconds(30));
try
{
    await foreach (var @event in client.Events.SubscribeAsync(cancellationToken: window.Token))
    {
        Console.WriteLine($"{@event.GetType().Name} ({@event.Type})");
    }
}
catch (OperationCanceledException failure) when (failure.CancellationToken == window.Token)
{
    Console.WriteLine("the 30-second window ended");
}
```

### Send a prompt and stream the reply

This is QuickStart's core, shortened. It picks a free model from the live catalog, opens the event
stream before it prompts, prints the reply's text as it arrives, and ends on the turn's execution
event. [QuickStart](https://github.com/opencode-dotnet/opencode-sdk-dotnet/tree/master/playground/quickstart)
is the full version: it waits out the server's startup and reports retries and failures.

```csharp
using OpenCode.Sdk;
using OpenCode.Sdk.Models;

await using var server = await OpenCodeServer.StartAsync();
using var client = server.CreateClient();

// A free model from the live catalog; no API key needed.
await client.LanguageModels.WaitForDefaultAsync();
var catalog = await client.LanguageModels.ListModelsAsync();
var model = catalog.Models.First(static m => m.ProviderId == "opencode" && m.Cost.All(static c => c.Input <= 0));

var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest
{
    Model = new ModelRef { Id = model.Id, ProviderId = model.ProviderId },
});

// Open the event stream before prompting: an event published before you listen is gone.
await using var events = client.Events.SubscribeAsync().GetAsyncEnumerator();
await events.MoveNextAsync();

await client.Sessions.GetSessionClient(created.Session.Id)
    .PromptAsync(new SessionPromptRequest { Text = "In one sentence, what is a typed SDK?" });

while (await events.MoveNextAsync())
{
    switch (events.Current)
    {
        case SessionTextDelta delta when delta.Data.SessionId == created.Session.Id:
            Console.Write(delta.Data.Delta);
            break;
        case SessionExecutionSucceeded done when done.Data.SessionId == created.Session.Id:
            Console.WriteLine();
            return;
        case SessionExecutionFailed failed when failed.Data.SessionId == created.Session.Id:
            Console.WriteLine($"the turn failed: {failed.Data.Error.Message}");
            return;
        case SessionExecutionInterrupted stopped when stopped.Data.SessionId == created.Session.Id:
            Console.WriteLine($"the turn was interrupted: {stopped.Data.Reason}");
            return;
    }
}
```

### A server you already run

```csharp
using var client = new OpenCodeClient(new OpenCodeClientOptions
{
    Endpoint = new Uri("http://127.0.0.1:4096"),
    Password = Environment.GetEnvironmentVariable("OPENCODE_PASSWORD"),
});
```

The client reads no environment variables of its own. The caller decides where the password comes
from, as opencode's own CLI does.

### The background service the CLI runs

`DiscoverAsync` reads the CLI's registration file with the CLI's own rules and checks the daemon's
authenticated status. It returns a handle that owns nothing: disposing it never stops the service
that other clients share. Null means no ready service. `EnsureAsync` starts one through the CLI's
own election when none is running.

```csharp
var server = await OpenCodeServer.DiscoverAsync();
if (server is null)
{
    return; // no ready registered service
}

using var client = server.CreateClient();
```

The [connection guide](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/connection-modes.md)
has every option, `EnsureAsync`, `StopAsync`, launcher output capture, and pairing another client.

### Dependency injection

The `OpenCodeDotNet.Sdk.Extensions` package provides `AddOpenCode` in the
`Microsoft.Extensions.DependencyInjection` namespace. It registers one `OpenCodeClient` singleton
and every sub-client, each only when that service type is not already registered. A consumer's earlier root-client or sub-client
registration wins, and repeated `AddOpenCode` calls add no duplicate client registrations. The
default root is thread-safe and owns its transport for the container's lifetime; default
sub-clients resolve from the registered root, so you can inject `SessionsClient`, `EventsClient`,
or `PtysClient` directly. See the [DI registration guide](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/connection-modes.md#-registering-with-dependency-injection).

For a non-web host, `Host` and `BackgroundService` come from the `Microsoft.Extensions.Hosting` package:

```bash
dotnet add package Microsoft.Extensions.Hosting
```

```csharp
var builder = Host.CreateApplicationBuilder(args);
builder.Services.AddOpenCode(options =>
{
    options.Endpoint = new Uri("http://127.0.0.1:4096");
    options.Password = Environment.GetEnvironmentVariable("OPENCODE_PASSWORD");
});
```

```csharp
internal sealed class SessionWorker(SessionsClient sessions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var page = await sessions.ListSessionsAsync(
            new SessionListRequest { Limit = 10, Order = ListOrder.Descending },
            cancellationToken: stoppingToken);

        Console.WriteLine($"{page.Sessions.Count} sessions, next cursor {page.Cursor.Next ?? "<none>"}");
    }
}
```

`AddOpenCode(IConfiguration)` binds the same options from a configuration section. Configuration
binding uses reflection, so that overload is annotated `[RequiresDynamicCode]` and
`[RequiresUnreferencedCode]`. Under trimming or native AOT, use the configure-action overload.

## 🛝 Playground

Four runnable apps show the SDK in real workflows:

- [QuickStart](https://github.com/opencode-dotnet/opencode-sdk-dotnet/tree/master/playground/quickstart): start in five minutes without an API key.
- [HQ](https://github.com/opencode-dotnet/opencode-sdk-dotnet/tree/master/playground/hq): see live sessions and answer permission asks in Blazor Server with Aspire.
- [Worker](https://github.com/opencode-dotnet/opencode-sdk-dotnet/tree/master/playground/worker): run the SDK in a hosted service.
- [AotCli](https://github.com/opencode-dotnet/opencode-sdk-dotnet/tree/master/playground/aotcli): ship a Native AOT tool.

They need the .NET SDK band that `global.json` pins and the opencode CLI, installed as [Installation](#-installation) describes.

## 📚 Documentation

| Guide | What it covers |
|---|---|
| [**The guide**](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/README.md) | Index of every page below, in reading order |
| [Getting started](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/getting-started.md) | Install, first call, and the shape of the client family |
| [Connection modes](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/connection-modes.md) | The standalone launcher, an external server, the background service, pairing, and DI registration |
| [Streaming](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md) | The live event stream and durable session event streams |
| [Terminals](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/terminals.md) | PTY and persistent-PTY sessions over the WebSocket doors |
| [Errors and responses](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/errors-and-responses.md) | Throwing versus `NoThrow`, and the typed error model |
| [Pagination](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/pagination.md) | Cursor-carrying list envelopes, `Enumerate*Async`, and its `Pages` |
| [Requests](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/requests.md) | Request records, the absent/null/set states of `Optional<T>`, query members, per-call location, and the permission, worktree, and shell-timeout members |
| [Experimental APIs](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/experimental.md) | What upstream serves as experimental, the `OPENCODE001` and `OPENCODE002` diagnostics, and how to opt in |

Architecture, decision records, and engineering policy live under
[`docs/`](https://github.com/opencode-dotnet/opencode-sdk-dotnet/tree/master/docs).
[CONTRIBUTING.md](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/.github/CONTRIBUTING.md) is where contributors start.

## 📌 Versions and support

- From `1.0.0`, a version follows the bump type of the upstream opencode release it pins: an upstream patch is an SDK patch, a minor a minor, a major a major. The SDK's own fixes and additions are a patch, and only a major breaks the hand-written surface. A release that moves the pin may change the generated protocol models the way upstream changed them; the CHANGELOG lists each such change as **breaking**.
  See [CHANGELOG.md](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/CHANGELOG.md).
- 📌 The SDK builds against a snapshot of an upstream release tag, never a live branch.
  [`spec/SNAPSHOT.md`](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/spec/SNAPSHOT.md)
  owns the exact pin and the refresh procedure.

## 🚀 Platform Compatibility & Quality Status

### Supported Platforms

- [.NET 11](https://dotnet.microsoft.com/download/dotnet/11.0) | [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0)
- [.NET Standard 2.0](https://docs.microsoft.com/en-us/dotnet/standard/net-standard)
- [.NET Framework 4.7.2 and Above](https://dotnet.microsoft.com/download/dotnet-framework)

Both packages target `netstandard2.0;net472;net10.0;net11.0`. The whole suite runs on `net472` on
Windows, real-process launcher tests included, and on `net10.0` and `net11.0` on Windows, Linux,
and macOS. A project whose target framework falls back to the `netstandard2.0` asset on a runtime
the packages do not target, such as `net8.0` or `net9.0`, gets a build warning;
`<SuppressTfmSupportBuildWarnings>true</SuppressTfmSupportBuildWarnings>` silences it. A .NET Framework project needs a newer C# version than its default;
the [getting-started guide](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/getting-started.md#net-framework-projects)
has the two properties to set.

### Build & Test Matrix

| Category | Platform/Type | Status | Description |
|----------|---------------|--------|-------------|
| **🔧 Build** | Cross-Platform | [![CI](https://github.com/opencode-dotnet/opencode-sdk-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/opencode-dotnet/opencode-sdk-dotnet/actions/workflows/ci.yml) | Matrix: Windows, Linux, macOS |
| **🧪 Tests** | Linux | [![Linux Tests](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Ftests%2Flinux%2Fopencode-dotnet%2Fopencode-sdk-dotnet%2Fmaster)](https://api.localstackfor.net/redirect/test-results/linux/opencode-dotnet/opencode-sdk-dotnet/master) | `net10.0`, `net11.0` |
| **🧪 Tests** | Windows | [![Windows Tests](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Ftests%2Fwindows%2Fopencode-dotnet%2Fopencode-sdk-dotnet%2Fmaster)](https://api.localstackfor.net/redirect/test-results/windows/opencode-dotnet/opencode-sdk-dotnet/master) | `net472` plus every modern target |
| **🧪 Tests** | macOS | [![macOS Tests](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Ftests%2Fmacos%2Fopencode-dotnet%2Fopencode-sdk-dotnet%2Fmaster)](https://api.localstackfor.net/redirect/test-results/macos/opencode-dotnet/opencode-sdk-dotnet/master) | `net10.0`, `net11.0` |
| **📡 Consumer leg** | Linux, Windows | [![Consumer leg](https://github.com/opencode-dotnet/opencode-sdk-dotnet/actions/workflows/consumer-leg.yml/badge.svg)](https://github.com/opencode-dotnet/opencode-sdk-dotnet/actions/workflows/consumer-leg.yml) | Weekly and on demand against the published `@opencode/cli` build for the pin: all `OpenCode.Sdk.Tests` on `net10.0`; AotCli publishes natively and inspects a repository; Worker exits with code 1 when its server ends; HQ's board shows a seeded session through headless Chrome |

### 📦 Package Status

| Package | NuGet.org | GitHub Packages |
|---------|-----------|-----------------|
| **OpenCodeDotNet.Sdk** | [![NuGet](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Fpackages%2Fnuget%2FOpenCodeDotNet.Sdk)](https://www.nuget.org/packages/OpenCodeDotNet.Sdk) | [![GitHub Packages](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Fpackages%2Fgithub%2Fopencode-dotnet%2FOpenCodeDotNet.Sdk%3Fprerelease%3Dtrue)](https://github.com/opencode-dotnet/opencode-sdk-dotnet/pkgs/nuget/OpenCodeDotNet.Sdk) |
| **OpenCodeDotNet.Sdk.Extensions** | [![NuGet](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Fpackages%2Fnuget%2FOpenCodeDotNet.Sdk.Extensions)](https://www.nuget.org/packages/OpenCodeDotNet.Sdk.Extensions) | [![GitHub Packages](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Fpackages%2Fgithub%2Fopencode-dotnet%2FOpenCodeDotNet.Sdk.Extensions%3Fprerelease%3Dtrue)](https://github.com/opencode-dotnet/opencode-sdk-dotnet/pkgs/nuget/OpenCodeDotNet.Sdk.Extensions) |

## Known Issues

- **The event bus has no replay.** `EventsClient.SubscribeAsync` is a live stream: events published
  while you are disconnected are lost, and a consumer slower than the producer can overflow and
  fail the stream. This is the server's contract, not an SDK limitation. See the
  [streaming guide](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md).

- **A CLI-started server's durable session event stream holds only the sync marker.** It reads from
  the server's event store, and the distributed `opencode` CLI starts its server without event
  persistence. No flag, environment variable, or configuration key turns it on (confirmed at the
  pin). `ExperimentalClient.ReadSessionLogAsync` does not
  fail: with or without `Follow`, it delivers one `log.synced` frame, normally materialized as
  `EventLogSynced`, and no durable events. For live session activity, subscribe to the live event
  stream and filter by session id. Persisted replay needs a host that embeds the opencode server
  library with persistence enabled.

- **On `net472` and `netstandard2.0`, some costs are larger than on modern targets.** A response body
  of 64 MiB or more gets a newly allocated buffer on every call. Smaller bodies over 1 MiB come from
  a pool the SDK keeps for the life of the process, which holds at most about 250 MiB after large
  responses or requests. A body with no declared length uses every smaller size on the way up, so
  one 33 MiB body can leave about 126 MiB held. On .NET Framework, a request with more than about
  21 MiB of text still allocates its buffer on every call. A request on the `netstandard2.0` asset
  isn't pooled at all.
  On .NET Framework, reading a JSON string over 1 MiB that contains an escape, such as a newline,
  allocates one extra array of its length each time.
  On Unix (Mono), owner-only files that discovery and `EnsureAsync` write get mode `0600` through a
  `chmod` child process rather than at creation, and a failed `chmod` fails the write. No CI leg runs
  .NET Framework or Mono on Unix.

- **A terminal's receiver queues frames that nobody reads**, so a slow or absent consumer can grow
  memory. See the
  [terminal lifetime contract](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/terminals.md).

## Developing

We appreciate contributions in the form of feedback, bug reports, and pull requests. Read
[CONTRIBUTING.md](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/.github/CONTRIBUTING.md)
first — it has the full gate and the commit convention.

```bash
git clone --recurse-submodules https://github.com/opencode-dotnet/opencode-sdk-dotnet.git
cd opencode-sdk-dotnet
dotnet build --configuration Release
```

`external/` holds read-only upstream submodules: protocol evidence, and the pinned server that the
fixture-backed tests run. Those tests also need Bun, the pinned server's dependencies, and
ripgrep; [`docs/engineering/quality-gates.md`](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/engineering/quality-gates.md)
has the versions and the full completion gate.

## Community

Got questions or wild feature ideas?

👉 Blank issues are disabled. Use the [missing-operation form](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/new?template=operation_gap.yml)
for a missing operation; use the [feature-idea form](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/new?template=feature_idea.yml)
for an idea, and the [bug form](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/new?template=bug_report.yml)
for anything else, including questions. A vulnerability goes through a private advisory instead, as [SECURITY.md](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/.github/SECURITY.md) says.

## Related Projects

- [opencode-mcp](https://github.com/opencode-dotnet/opencode-mcp): a Model Context Protocol (MCP) server project for opencode.
- [opencode-agents-ai](https://github.com/opencode-dotnet/opencode-agents-ai): a Microsoft Agent Framework provider project for opencode.

## Changelog

Please refer to [`CHANGELOG.md`](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/CHANGELOG.md) to see the complete list of changes for each release.

## License

Licensed under MIT, see [LICENSE](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/LICENSE) for the full text. Content derived from
upstream opencode carries its own notice in
[THIRD-PARTY-NOTICES.md](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/THIRD-PARTY-NOTICES.md).
