# opencode SDK for .NET

[![License: MIT](https://img.shields.io/badge/License-MIT-yellow.svg)](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/LICENSE) [![NuGet](https://img.shields.io/nuget/vpre/OpenCodeDotNet.Sdk)](https://www.nuget.org/packages/OpenCodeDotNet.Sdk) [![CI](https://github.com/opencode-dotnet/opencode-sdk-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/opencode-dotnet/opencode-sdk-dotnet/actions/workflows/ci.yml) [![Linux Tests](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Ftests%2Flinux%2Fopencode-dotnet%2Fopencode-sdk-dotnet%2Fmaster)](https://api.localstackfor.net/redirect/test-results/linux/opencode-dotnet/opencode-sdk-dotnet/master)

> **🚀 Quick Start**: [Install](#-installation) | [Quick start](#-quick-start) | [Guide](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/getting-started.md) | [API coverage](#-api-coverage)

> **Unofficial.** This project is not affiliated with or endorsed by the
> [opencode](https://opencode.ai) team.

> **📦 New package ids and a new home (`0.9.0-preview.5`).** The packages are now
> [`OpenCodeDotNet.Sdk`](https://www.nuget.org/packages/OpenCodeDotNet.Sdk) and
> [`OpenCodeDotNet.Sdk.Extensions`](https://www.nuget.org/packages/OpenCodeDotNet.Sdk.Extensions);
> `OpenCodeAI.Sdk` and `OpenCodeAI.Sdk.Extensions` are deprecated. Namespaces are unchanged, so
> only the `PackageReference` changes. The repository moved to the `opencode-dotnet` organization,
> and old links redirect.

A strongly typed .NET client for the [opencode](https://github.com/anomalyco/opencode) server — the
HTTP API that every opencode front-end (TUI, desktop, web UI, plugins) uses. The SDK speaks the
OpenCode 2.x API; the 1.x server API is not supported.

```csharp
await using var server = await OpenCodeServer.StartAsync();
using var client = server.CreateClient();

var created = await client.Sessions.CreateSessionAsync(new SessionCreateRequest { Title = "hello from .NET" });
Console.WriteLine($"session {created.Session.Id}");
```

---

## 🎉 Project Status

**Pre-1.0, and the protocol surface is complete.** **All 140 operations** in the pinned OpenAPI
snapshot are callable — 138 as generated HTTP calls, and the two terminal WebSocket connections
through hand-written transports. All three connection modes work: a private server the SDK
starts, a server you already run, and the background service that the opencode CLI registers.

- 🚧 Releases are `0.9.0-preview.N`. A reviewed baseline locks the public surface, but it can still
  change before `1.0.0`. See [CHANGELOG.md](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/CHANGELOG.md).
- 📌 The SDK builds against a snapshot of an upstream release tag, never a live branch.
  [`spec/SNAPSHOT.md`](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/spec/SNAPSHOT.md)
  owns the exact pin and the refresh procedure.
- 🔜 An **MCP server** over this SDK is planned, not started. It will live in its own repository
  in the [opencode-dotnet](https://github.com/opencode-dotnet) organization.

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
  declare `IsAotCompatible` on `net10.0`.
- **A pinned protocol.** Each refresh to a new upstream release comes with a receipt, so a
  regeneration is a diff that you can review.

## 🧭 API Coverage

`OpenCodeClient` exposes one sub-client per area of the API. Every sub-client is also injectable on
its own when you use dependency injection.

| Area | Entry point | Guide |
|---|---|---|
| Sessions, messages, prompts, session logs | `client.Sessions` | [Requests](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/requests.md), [Pagination](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/pagination.md) |
| The global event bus | `client.Events` | [Streaming](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md) |
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
| Operations upstream marks experimental | `client.Experimental` | — |

The two terminal connections (`pty.connect`, `persistentPty.connect`) are WebSocket upgrades that
the HTTP pipeline cannot carry. They are fully usable through the hand-written `PtySession` and
`PersistentPtySession` types. Nothing is declined:
[`src/OpenCode.Sdk/.generation-incomplete`](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/src/OpenCode.Sdk/.generation-incomplete)
is the machine-readable map that the build reads.

## 📦 Installation

```bash
dotnet add package OpenCodeDotNet.Sdk --prerelease
dotnet add package OpenCodeDotNet.Sdk.Extensions --prerelease   # dependency injection, optional
```

> **The package id is not the namespace.** You install `OpenCodeDotNet.Sdk`, and you write
> `using OpenCode.Sdk;`. nuget.org reserves the `OpenCode.` id prefix for an unrelated owner, so
> only the `PackageReference` carries the `OpenCodeDotNet` name. The earlier ids,
> `OpenCodeAI.Sdk` and `OpenCodeAI.Sdk.Extensions`, receive no further versions.

You also need the `opencode` CLI, from the `@opencode/cli` npm scope. Install the release that this
repository pins — later releases usually work, but they are not what the tests run against:

```sh
npm install -g @opencode/cli@2.0.22
```

You do not have to start it. `OpenCodeServer.StartAsync()` in the [quick start](#-quick-start)
starts a private server for you.

### Nightly builds (GitHub Packages)

Every code push to `master` publishes `0.9.0-nightly.{yyyyMMdd}.{shortSha}` to GitHub Packages:

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

<details>
<summary>Keep the token out of shell history with a <code>nuget.config</code></summary>

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

</details>

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
await foreach (var @event in client.Events.SubscribeAsync(window.Token))
{
    Console.WriteLine($"{@event.GetType().Name} ({@event.Type})");
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

`OpenCode.Sdk.Extensions` registers one singleton client that owns its transport for the
container's lifetime. The client is thread-safe. Every sub-client resolves from that same instance,
so you can inject `SessionsClient`, `EventsClient`, or `PtysClient` directly.

```csharp
builder.Services.AddOpenCode(options =>
{
    options.Endpoint = new Uri("http://127.0.0.1:4096");
    options.Password = Environment.GetEnvironmentVariable("OPENCODE_PASSWORD");
});

internal sealed class SessionWorker(SessionsClient sessions) : BackgroundService
{
    protected override async Task ExecuteAsync(CancellationToken stoppingToken)
    {
        var page = await sessions.ListSessionsAsync(
            new SessionListRequest { Limit = "10", Order = ListOrder.Descending },
            cancellationToken: stoppingToken);

        Console.WriteLine($"{page.Sessions.Count} sessions, next cursor {page.Cursor.Next ?? "<none>"}");
    }
}
```

`AddOpenCode(IConfiguration)` binds the same options from a configuration section. Configuration
binding uses reflection, so that overload is annotated `[RequiresDynamicCode]` and
`[RequiresUnreferencedCode]`. Under trimming or native AOT, use the configure-action overload.

## 📚 Documentation

| Guide | What it covers |
|---|---|
| [**The guide**](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/README.md) | Index of every page below, in reading order |
| [Getting started](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/getting-started.md) | Install, first call, and the shape of the client family |
| [Connection modes](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/connection-modes.md) | The standalone launcher, an external server, the background service, pairing, and DI registration |
| [Streaming](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md) | The global event bus and per-session server-sent event streams |
| [Terminals](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/terminals.md) | PTY and persistent-PTY sessions over the WebSocket doors |
| [Errors and responses](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/errors-and-responses.md) | Throwing versus `NoThrow`, and the typed error model |
| [Pagination](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/pagination.md) | Cursor-carrying list envelopes, `Enumerate*Async`, and its `Pages` |
| [Requests](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/requests.md) | Request records, the absent/null/set states of `Optional<T>`, query members, per-call location, and the permission, worktree, and shell-timeout members |

Architecture, decision records, and engineering policy live under
[`docs/`](https://github.com/opencode-dotnet/opencode-sdk-dotnet/tree/master/docs). Start at
[`AGENTS.md`](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/AGENTS.md) for the
internals.

## 🚀 Platform Compatibility & Quality Status

### Supported Platforms

- [.NET 10](https://dotnet.microsoft.com/download/dotnet/10.0) | [.NET 9](https://dotnet.microsoft.com/download/dotnet/9.0) | [.NET 8](https://dotnet.microsoft.com/download/dotnet/8.0)
- [.NET Standard 2.0](https://docs.microsoft.com/en-us/dotnet/standard/net-standard)
- [.NET Framework 4.7.2 and Above](https://dotnet.microsoft.com/download/dotnet-framework)

Both packages target `netstandard2.0;net472;net8.0;net9.0;net10.0`. The whole suite runs on
`net472` on Windows, real-process launcher tests included, and on `net8.0`, `net9.0`, and `net10.0`
on Windows, Linux, and macOS. A .NET Framework project needs a newer C# version than its default;
the [getting-started guide](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/getting-started.md#net-framework-projects)
has the two properties to set.

### Build & Test Matrix

| Category | Platform/Type | Status | Description |
|----------|---------------|--------|-------------|
| **🔧 Build** | Cross-Platform | [![CI](https://github.com/opencode-dotnet/opencode-sdk-dotnet/actions/workflows/ci.yml/badge.svg)](https://github.com/opencode-dotnet/opencode-sdk-dotnet/actions/workflows/ci.yml) | Matrix: Windows, Linux, macOS |
| **🧪 Tests** | Linux | [![Linux Tests](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Ftests%2Flinux%2Fopencode-dotnet%2Fopencode-sdk-dotnet%2Fmaster)](https://api.localstackfor.net/redirect/test-results/linux/opencode-dotnet/opencode-sdk-dotnet/master) | `net8.0`, `net9.0`, `net10.0` |
| **🧪 Tests** | Windows | [![Windows Tests](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Ftests%2Fwindows%2Fopencode-dotnet%2Fopencode-sdk-dotnet%2Fmaster)](https://api.localstackfor.net/redirect/test-results/windows/opencode-dotnet/opencode-sdk-dotnet/master) | `net472` plus every modern target |
| **🧪 Tests** | macOS | [![macOS Tests](https://img.shields.io/endpoint?url=https%3A%2F%2Fapi.localstackfor.net%2Fbadges%2Ftests%2Fmacos%2Fopencode-dotnet%2Fopencode-sdk-dotnet%2Fmaster)](https://api.localstackfor.net/redirect/test-results/macos/opencode-dotnet/opencode-sdk-dotnet/master) | `net8.0`, `net9.0`, `net10.0` |
| **📡 Consumer leg** | Linux, Windows | [![Consumer leg](https://github.com/opencode-dotnet/opencode-sdk-dotnet/actions/workflows/consumer-leg.yml/badge.svg)](https://github.com/opencode-dotnet/opencode-sdk-dotnet/actions/workflows/consumer-leg.yml) | Weekly and on demand: the same suite against the published `@opencode/cli` build for the pin |

### 📦 Package Status

| Package | NuGet.org | GitHub Packages |
|---------|-----------|-----------------|
| **OpenCodeDotNet.Sdk** | [![NuGet](https://img.shields.io/nuget/vpre/OpenCodeDotNet.Sdk)](https://www.nuget.org/packages/OpenCodeDotNet.Sdk) | [![GitHub Packages](https://img.shields.io/badge/GitHub%20Packages-nightly-blue)](https://github.com/opencode-dotnet/opencode-sdk-dotnet/pkgs/nuget/OpenCodeDotNet.Sdk) |
| **OpenCodeDotNet.Sdk.Extensions** | [![NuGet](https://img.shields.io/nuget/vpre/OpenCodeDotNet.Sdk.Extensions)](https://www.nuget.org/packages/OpenCodeDotNet.Sdk.Extensions) | [![GitHub Packages](https://img.shields.io/badge/GitHub%20Packages-nightly-blue)](https://github.com/opencode-dotnet/opencode-sdk-dotnet/pkgs/nuget/OpenCodeDotNet.Sdk.Extensions) |

## Known Issues

- **The event bus has no replay.** `EventsClient.SubscribeAsync` is a live stream: events published
  while you are disconnected are lost, and a consumer slower than the producer can overflow and
  fail the stream. This is the server's contract, not an SDK limitation. See the
  [streaming guide](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md).

- **A CLI-started server's session log holds only the sync marker.** The per-session log reads from
  the server's event store, and the distributed `opencode` CLI starts its server without event
  persistence. No flag, environment variable, or configuration key turns it on (confirmed at the
  pin; observed on `@opencode/cli@2.0.15`). `SessionClient.GetLogAsync` does not fail: with or
  without `Follow`, it delivers one `EventLogSynced` marker and no durable events. For live session
  activity, subscribe to the global event bus and filter by session id. Persisted replay needs a
  host that embeds the opencode server library with persistence enabled.

- **On `net472` and `netstandard2.0`, two costs are larger than on modern targets.** A response body
  over 1 MB is copied once at wire size, because the downlevel array pool caps its buckets at 1 MB.
  On Unix (Mono), owner-only files that discovery and `EnsureAsync` write get mode `0600` through a
  `chmod` child process, which does not quote the path or check the exit code. No CI leg runs .NET
  Framework or Mono on Unix.

- **Each terminal connection allocates a 16 KiB receive buffer**, reused across reads. The receiver
  also queues frames that nobody reads, so a slow or absent consumer can grow memory. See the
  [terminal lifetime contract](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/architecture/client-runtime.md).

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

[`tests/OpenCode.Sdk.Sandbox`](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/tests/OpenCode.Sdk.Sandbox/README.md)
is a committed playground that drives the SDK against a real `opencode serve` under a debugger.

## Community

Got questions or wild feature ideas?

👉 Open an [issue](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues) — bug reports,
questions, and proposals all land there for now.

## Changelog

Please refer to [`CHANGELOG.md`](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/CHANGELOG.md) to see the complete list of changes for each release.

## License

Licensed under MIT, see [LICENSE](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/LICENSE) for the full text. Content derived from
upstream opencode carries its own notice in
[THIRD-PARTY-NOTICES.md](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/THIRD-PARTY-NOTICES.md).
