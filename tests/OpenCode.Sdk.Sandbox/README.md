# OpenCode.Sdk.Sandbox

A committed local playground for driving the SDK against a real `opencode serve` under a
debugger. It rides the repository's full convention set (analyzers, `.editorconfig`, format
gate) — unlike `.scratchpad/`, which remains the home for throwaway prototypes that answer a
question and disappear.

## Running

It needs only the `opencode` CLI on `PATH`. By default the sandbox connects the way the CLI does:
`OpenCodeServer.EnsureAsync` reuses your registered background service, or starts one when none is
usable. There is no endpoint, password, or port to set, and the server uses your own opencode
configuration, so the providers you configured are there. A service the sandbox started keeps
running afterwards, exactly as it would after `opencode`; `opencode service stop` ends it.

```sh
dotnet run --project tests/OpenCode.Sdk.Sandbox                  # the breadth walkthrough
dotnet run --project tests/OpenCode.Sdk.Sandbox -- --stream      # follow one session's log
dotnet run --project tests/OpenCode.Sdk.Sandbox -- --events      # the global event bus
dotnet run --project tests/OpenCode.Sdk.Sandbox -- --paginate <sessionId>
dotnet run --project tests/OpenCode.Sdk.Sandbox -- --standalone  # a private server the SDK starts
```

The `Properties/launchSettings.json` profiles carry the same flags for F5, and the first one runs
the walkthrough.

**A server you run yourself:** add `--endpoint <url>`. Its password comes from `OPENCODE_PASSWORD`,
the variable `opencode serve` itself reads:

```sh
OPENCODE_PASSWORD=your-password opencode serve --hostname 127.0.0.1 --port 4096
OPENCODE_PASSWORD=your-password dotnet run --project tests/OpenCode.Sdk.Sandbox -- --endpoint http://127.0.0.1:4096
```

The walkthrough asserts what the server answers, so it needs a server with a provider configured:
its early session legs answer 500 on a provider-less server, such as an isolated test server, and
the PTY and persistent PTY legs after them are then never reached. `PersistentPtyLiveTests` is what
proves the persistent PTY round trip; on a Windows workstation that needs a Linux-hosted server
(`docs/engineering/developing-on-windows.md`).

The stream example composes through the Extensions package:
`AddOpenCode` registers one singleton client family, and the Generic Host injects its
`SessionsClient` and `ExperimentalClient` into `SessionLogWorker`. The worker creates a session
and follows `ExperimentalClient.ReadSessionLogAsync` through the host's normal `stoppingToken`.
Each frame is logged with its generated runtime type. Ctrl+C stops the host, cancels the
open response read, and disposes the singleton SDK transport with the container.

The event mode injects `EventsClient` into `EventBusWorker` and consumes the volatile global bus.
After it reports that the bus is opening, trigger server activity from another process; running the
standing breadth walkthrough without a mode flag creates a session and supplies representative
events. The bus has no replay or resume contract: events during disconnection are missed, and a slow
consumer can overflow and fail the stream. Ctrl+C exercises the same host cancellation path.

The mode flags are mutually exclusive. Run without one to keep driving the
standing breadth walkthrough: status, session create/list/get, message list, experimental export
with its sanitize query, permission create/get/reply, compact and fork, interrupt and DELETE
revert-clear, experimental instructions and MCP mutations, PTY update, and a typed
`FormNotFoundError` through NoThrow. The envelope leg reads `Vcs.ListBranchesAsync`, the resolved
directory, the session-active dictionary, `Server.GetInfoAsync().ServerInfo.Urls`, and session
context. The PTY and persistent PTY legs use the same Extensions registration.

The PTY leg (`PtySessionWalkthrough`) is the hand-written family's live proof (ADR-0021). It
creates a PTY, lists the family, mints a connect ticket through the token door — whose
`x-opencode-ticket` header the SDK applies internally — and then opens the WebSocket session
**ticket-less**, carrying the client's Basic credential on the upgrade request, which is the
designed non-browser path. It records the replay frames and the single cursor frame that ends
the replay, writes `echo hello`, reads until the terminal echoes it, reconnects at the observed
cursor to show that a resume replays only what came after it, and finally removes the PTY while a
read is in flight so the normal close ends the enumeration rather than faulting it.

The persistent PTY leg (`PersistentPtyWalkthrough`) follows it over the second hand-written family,
whose live socket is the inverse wire: binary output frames and framed binary input. Which arm runs
is the server's own answer rather than a flag. Where the `opencode-pty` daemon exists (Linux and
macOS; the daemon ships no win32 package) it creates a terminal for the walkthrough's session,
lists it, attaches as controller, writes `echo sdk-live` terminated by a line feed — Enter on these
terminals' Unix line discipline — reads until the output carries the echo, resizes to 100x30 and
reads the `resized` frame the server answers with, reads the same terminal back over HTTP (the
controller attach is what selected it for that route), and removes it. Where the daemon does not
exist, `create` is the only route that fails, so the leg records the declared 503 naming
`opencode-pty` beside the daemon-absent answers of the others:

```text
ppty-create: status=503 service=opencode-pty error=ServiceUnavailableError
ppty-list:   status=200 ptys=0
ppty-read:   status=200 read=<null>
ppty-handoff: status=200 handoff=<null>
ppty-shutdown: status=204 isError=False
```

## Standalone server demo (`--standalone`)

`StandaloneServerWalkthrough` is the launcher demo: the SDK starts a private `opencode serve` and
owns it through `OpenCodeServer.StartAsync` (the standalone-start connection mode;
`docs/architecture/client-runtime.md` §Connection modes), resolved from `PATH` the way a shell
resolves it, then calls `CreateClient()` and `Server.GetInfoAsync` under a 5-second-bounded probe.
Disposing the server stops it. The distributed-build consumer leg runs this mode against the
published CLI.

Status is followed by `ModelSelectionWalkthrough`, the executable "choosing a model" recipe.
It observes provider/model catalogs, creates a session with an available `ModelRef { ProviderId, Id }`,
and removes it. Catalogs can still be incomplete during asynchronous plugin activation
(`CONTEXT.md`, Plugin activation). An empty observation prints `model: none currently available`
and returns successfully; it does not prove that activation has settled or that no provider is
configured.

## Pointing the live suite at another build of the server

`PinnedOpenCodeServerFixture` normally starts the pinned submodule source under bun.
`OPENCODE_SDK_TESTS_SERVER_COMMAND` replaces that command and changes nothing else — the same
isolated XDG roots, the same repository-owned RPC plugin seeded into the server's configuration,
the same launcher-owned readiness and teardown, the same retained logs — so the whole suite can be
run against a different build of the same server:

```powershell
$env:OPENCODE_SDK_TESTS_SERVER_COMMAND = "opencode|serve"
dotnet test tests/OpenCode.Sdk.Tests --configuration Release --no-build --framework net10.0
```

The value is `|`-separated so that a path with spaces survives as one token. The first token is resolved by the SDK's own launcher from `PATH`
(`PATHEXT` included, so an npm `.cmd` shim starts), so a bare `opencode` is the whole value it
needs, and the fixture prints which command it started. The distributed-build consumer leg
(`.github/workflows/consumer-leg.yml`) is the standing user: that is how the published
`@opencode/cli` build gets this suite run against it.

Plugin check/update tests use `OwnedPinnedOpenCodeServerFixture`, which owns its server even when an
external endpoint is configured, so their configuration is package-free by construction. It still
honors `OPENCODE_SDK_TESTS_SERVER_COMMAND`, so the distributed-build lane exercises these
operations. `docs/engineering/testing-style.md` owns what every owned server is isolated from.
Plugin listing asserts on both branches: an owned server carries its builtins plus the seeded RPC
plugin, and an external endpoint cannot carry that plugin. RPC tests prove `rpc.unavailable`
through a real call, whose upstream handler awaits activation, before checking absence from
inventory. Owned event and inventory tests wait for the exact seeded RPC plugin and assert its
local path and active state.

## Live legs against a server on another host

The same external-endpoint mode runs the suite against a server hosted elsewhere. The standing
case is a Windows workstation driving the persistent PTY legs against a WSL2-hosted server, because
the `opencode-pty` daemon ships no win32 package; that recipe and its gotchas live in
`docs/engineering/developing-on-windows.md`. The sandbox can be pointed at such an endpoint with
`--endpoint`, but it is not the proof: the walkthrough's earlier session legs answer 500 on a
provider-less isolated server, so it never reaches the persistent PTY leg there.
