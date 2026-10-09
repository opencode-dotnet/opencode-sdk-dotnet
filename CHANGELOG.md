# opencode .NET SDK Change Log

This document outlines the changes, updates, and important notes for the opencode SDK for .NET.
Each released version links straight to its GitHub Release tag.

## [Unreleased]

Nightly builds of `master` are on
[GitHub Packages](README.md#nightly-builds-github-packages) as
`0.9.0-nightly.{yyyyMMdd}.{shortSha}`.

## [0.9.0-preview.8] - 2026-10-09

**The pin moves to upstream release tag `v2.0.26`, and all 142 operations it exposes are
callable — 140 as generated HTTP calls, and the two terminal WebSocket connections through
hand-written transports.** An integration can connect through an external credential source, and
a 2.0.26 server's external credentials now decode; the experimental policy action `permission` is
renamed `tool.use`, the one breaking change, which comes first. On Windows a standalone server now
runs as upstream's launcher runs it: created suspended in a kill-on-close job, so it ends with its
owner however the owner ends; given its three standard handles and nothing else; read through
overlapped pipes; and ended at once with `taskkill /T /F`. `net8.0` and `net9.0` still leave in an
upcoming preview; see the important notes.

### 💥 Breaking changes

- **`ConfigInfoExperimentalPoliciesAction.Permission` is now `ToolUse`.** Upstream 2.0.26 renames
  the experimental policy action `permission` to `tool.use` and adds `integration.use`, which gates
  MCP servers and skills, so the enum's members are `ProviderUse`, `ToolUse`, and `IntegrationUse`.
  A 2.0.26 server drops a `permission` policy entry when it loads its config; against an older
  server whose config still has one, reading the config fails to decode.

### ✨ Added

- **The accepted snapshot moved to upstream release tag `v2.0.26`**
  (`9b4ec5714d481559990db0a816d5dec19541a814`), published as `@opencode/cli@2.0.26`; install it
  with `npm install -g @opencode/cli@2.0.26`.
- **An integration can be connected through an external credential source.**
  `IntegrationClient.ConnectWithExternalCredentialsAsync(request)` (`integration.connect.external`)
  runs an external authentication method, named by `MethodId` with an optional form `Answer` and
  `Label`, and stores a reference to its credential source. The integration lists such a method
  as `IntegrationExternalMethod`, and the stored credential reads as `CredentialExternal`
  (`MethodId`, `Metadata`). The route helper is
  `OpenCodeRoutes.Integrations.ConnectWithExternalCredentials`. Behaviour change: on
  `0.9.0-preview.7` a 2.0.26 server's `external` method surfaced as `UnknownIntegrationMethod`, and
  its `external` credential value as `UnknownCredentialValue`.
- **`ConnectionCredentialInfoMethod.External`** names a credential stored through an external
  method. Before, a server that returned one failed the decode of its connection.
- **`ModelCompatibility.SupportsThinkingBlockBinding` and `SupportsEffortUpdates`** carry the two
  compatibility flags upstream 2.0.26 adds.

### 🔧 Changes

- **On Windows a standalone server is placed in a job that ends it with its owner, however the owner
  ends.** `StartAsync` creates the server suspended, places it in a process-wide job that ends it
  when the host process exits, and only then lets it run, as upstream's launcher places its server
  in a job. Behaviour change: a server that ignores the end-of-stream of its stdin no longer
  outlives an owner that crashed or was killed before disposing it. Descendants the server starts
  detached stay outside the job and keep running; the other children of a server built on Bun or
  Node end with it, because its runtime places them in a job of its own under the launcher's. The
  job also ends a server that crashes on an unhandled exception at once, without a Windows Error
  Reporting dialog, as upstream's job does, and any window the server opens starts hidden. A server
  the kernel refuses to place in the job with `ERROR_ACCESS_DENIED`, as under a host job that cannot
  nest, runs outside it, as upstream's does, and then ends with a crashed owner only through its
  stdin lease. Any other refusal, a job that cannot be created, and a server that cannot be resumed
  fail the start with `OpenCodeServerException` and leave nothing running.
- **With the default npm install, pass the path of `opencode.exe` in `Command` for that guarantee.**
  The bare `opencode` resolves to npm's `opencode.cmd` shim, which runs through `cmd.exe`: the job
  then holds `cmd.exe`, and the real server, `cmd.exe`'s child, ends with a crashed owner only
  through its stdin lease, as upstream's own launcher of a `.cmd` file arranges it.
- **Disposing a standalone server on Windows ends the server and its whole process tree at once
  with `taskkill /T /F`, as upstream does, instead of closing its stdin and waiting.** Behaviour
  changes:
  - The server no longer runs its own graceful shutdown on disposal: it exits with code 1, where
    the stdin end-of-stream used to end it with code 0. Stdin now closes last, as on Linux and
    macOS, so an `Output` collector no longer receives the lines a server writes when its stdin
    ends.
  - Every live descendant of the server is ended at every close, detached ones included; the
    descendants used to survive a normal close.
  - Disposal takes the `taskkill` run, about 300 ms, where a server that left on its stdin's
    end-of-stream used to take about 10 ms.
  - `GracefulShutdownTimeout` now starts with that first kill and bounds it: when the server is
    still running at its end, the same kill runs once more, and `TerminateProcess` ends the server
    with code 1 when a kill fails. A disposal takes at most the grace plus 26 seconds, the output
    drain and the release of the output readers included.
  - On `net8.0` and later the forced end is `taskkill`, exit code 1, where it was the runtime's tree
    kill, exit code -1.
  - A server that already exited is not tree-killed, and a failed start ends the server the same
    way, with the configured grace, so a canceled or timed-out `StartAsync` can take up to the
    grace plus 26 seconds to throw.
  - On `net8.0` and later the descendants of a server that exits before readiness are no longer
    ended: the runtime's tree kill used to end them, but `taskkill` reaches no descendant of an
    exited root, so it does not run.
- **On Windows the server no longer inherits the host's own inheritable handles.** It receives its
  three standard handles and nothing else, so a parent process that captures the host's output
  sees it end when the host exits, even while a server it started still runs. Behaviour change:
  the server also no longer receives the handles the host inherited from its own ancestors.
- **On Windows the server's output is read without a dedicated thread, on every target.** Its
  stdout and stderr are overlapped pipes whose reads complete on the I/O completion port, on .NET
  Framework too; the two reader threads each server used to hold are gone.
- **`StartAsync` on Windows fails at once when the server closes its stdout before readiness and
  keeps running**, as on Linux and macOS, where it used to wait for the readiness timeout. A
  readiness line that arrives together with the exit now always starts the server, where the exit
  could win before. A server that exits before readiness while a process it started holds its
  stdout fails the start after a drain of at most one second instead of two.
- **A negative Windows exit code is reported with its hexadecimal form.** A server that crashes
  before readiness is reported as `exited with code -1073741819 (0xC0000005)`, where the message
  used to give only the decimal value.
- **A whitespace-only `WorkingDirectory` counts as none on Windows too.** The server then starts in
  the host's directory, as on Linux and macOS, where the blank value used to be passed to the
  process start as the server's directory.
- **`OPENCODE_PRINT_LOGS=1` in the host's environment hands the server the host's own stderr on
  Windows too**, as upstream's launcher does; no stderr is then collected or quoted in a startup
  failure. Diagnostics-mode behaviour, as upstream's: the server then also shares the host's
  console, so a Ctrl+C in that console reaches the server, even when the host handles it.
- **A NUL in a command entry or an environment entry is refused with `ArgumentException` on Windows
  too**, before anything is spawned, and a spawn that fails in a `WorkingDirectory` names the
  directory.
- **`StopAsync` and the Ensure replacement act on the service registered when they stop it.** Like
  upstream's client, they read the registration again before preparing or clearing the terminal
  handoff, and end the service that file names then. Behaviour change: a service that registered
  while `StopAsync` was asking the old one to shut its terminals down is the one stopped, where it
  used to be left running.
- **The service config's `remote` key is read as upstream reads it.** Remote access is set only
  through the CLI (`opencode service set remote true`, or `opencode pair --remote`); the SDK reads
  the key and never writes it. While the service's tunnel is attached, its remote URL is listed in
  `ServerInfo.Urls`. Behaviour change: a `remote` that is neither an object holding a string
  `route` nor the legacy boolean makes the whole document no config, as upstream decides, so its
  `env` overlay no longer applies, and a document with a legacy boolean still reads but is no
  longer migrated to a named channel's file; `0.9.0-preview.7` ignored `remote` in both cases.
- **A PTY connect refused with HTTP 401 or 403 names the credential or the origin.** Upstream
  2.0.26 checks the request's origin on every connect, before it looks the PTY up. Behaviour
  change: the `OpenCodeTransportException` message now ends "the request's credential or origin
  was rejected", as the persistent PTY's message already did, and a refused origin is reported as
  such even for a PTY that does not exist.
- **`FileSystem.ReadFileAsync` with a path that leaves the location answers the declared 404 with a
  typed `FileNotFoundError`.** Upstream 2.0.26 refuses such a path, and one it cannot decode, as a
  missing file. Behaviour change: the status is 404, not an undeclared 5xx, and `Error` is a
  `FileNotFoundError`.
- **A server bound to every interface lists its loopback URL first in `ServerInfo.Urls`.** Upstream
  2.0.26 puts `http://127.0.0.1:{port}` (or `http://[::1]:{port}`) before the interface addresses
  of a server bound to `0.0.0.0` or `::`.

### 🐛 Fixes

- **`OpenCodeServer.EnsureAsync` starts the background service on Windows the way upstream's client
  does: detached, with no console, and hidden.** The service it started had a windowless console of
  its own instead, and no hidden show state for a window it might open. It now starts with no
  console at all, still in a process group of its own, and with any window it opens starting hidden.
  Behaviour change: the background service an Ensure call starts on Windows no longer has a console.
- **The background service's contender on Windows exposes its stderr pipe to processes the host
  starts only while it is being created.** The pipe's write end was inheritable from its creation,
  so a `Process.Start` on another thread of the host could inherit it and hold the contender's
  stderr open after the contender ended. It is now inheritable only while the contender is being
  created, under the lock every SDK spawn takes.

### 📋 Important Notes

- **`net8.0` and `net9.0` leave the packages, and `net11.0` joins, in an upcoming preview,** ahead
  of their end of support on 2026-11-10; this release still ships them. The packages will then
  target `netstandard2.0;net472;net10.0;net11.0`. An app on .NET 8 or 9 still installs through the
  `netstandard2.0` asset, but that asset is a compatibility asset, not a supported runtime: support
  and testing cover the targeted runtimes only, and .NET 5–7 are not supported. Plan the move to
  .NET 10.
- **Upstream's committed OpenAPI document is still stale at `v2.0.26`**
  ([anomalyco/opencode#53105](https://github.com/anomalyco/opencode/issues/53105)). It lacks
  `LocationNotFoundError`, and with it the 404 that location-scoped operations declare for a missing
  location, and it lacks `integration.connect.external`. This SDK's snapshot comes from upstream's
  own pinned generator, so a diff against that committed file shows them here; they are real and
  the server answers them.

## [0.9.0-preview.7] - 2026-10-06

**The pin moves to upstream release tag `v2.0.23`, and all 141 operations it exposes are
callable — 139 as generated HTTP calls, and the two terminal WebSocket connections through
hand-written transports.** A repository can now be initialized, a location whose directory does
not exist answers a typed 404, and the server says whether it can run persistent terminals.
2.0.23 tags every error, so `WorktreeError` changes shape and an error body marked only by `name`
no longer decodes, and `EnsureAsync` no longer replaces a service whose health protocol is
incompatible; the breaking changes come first. On Linux and macOS a standalone server now runs in
a session of its own and is ended as upstream's launcher ends it, a `SIGTERM` to its process group
before the `SIGKILL`; Windows keeps its earlier path. The background-service doors follow the
2.0.23 client, the generated `ToString()` masks every configured header and environment map, the
open provider options, and unrecognized union values that can carry a secret, and both packages
declare `IsAotCompatible` on `net8.0` and `net9.0` too. `net8.0` and `net9.0` still leave in an
upcoming preview; see the important notes.

### 💥 Breaking changes

- **`WorktreeError` is a tagged error.** Upstream 2.0.23 declares it with `_tag` beside its `name`
  and `data`, so `Tag` reads `_tag` and a new required `Name` carries `name`. A body without `_tag`,
  from a server older than 2.0.23, no longer decodes to a typed error: `Error` is null and
  `RawBody` keeps the body.
- **`_tag` is the only error marker.** Every error the 2.0.23 contract declares is tagged, so a
  `name` is no longer read as an error's marker:
  - Any error body whose only marker is `name`, not just a worktree failure, no longer decodes to
    `UnknownOpenCodeError`: `Error` is null and `RawBody` keeps the body.
  - The public `UnknownOpenCodeError(tag, payload)` constructor throws `ArgumentException` for a
    payload whose only marker is `name`; the payload must carry the tag under `_tag`.
- **`OpenCodeServer.EnsureAsync` throws `OpenCodeServerException` on a service whose health
  protocol is incompatible, where it used to replace it.** A registered daemon that answers
  `/api/info` with an authenticated 404 is left running and the call throws, unless the call's
  version requirement is unmet, which still replaces it. Stop it explicitly with `StopAsync` to
  replace it.
- **`OpenCodeServer.StartAsync` refuses POSIX systems other than Linux and macOS, FreeBSD among
  them,** with `OpenCodeServerException` before anything is spawned; they used to start through
  `Process`. On Linux and macOS a NUL in a command entry or an environment entry is now refused
  with `ArgumentException`, because the C string it is passed as would end there.

### ✨ Added

- **The accepted snapshot moved to upstream release tag `v2.0.23`**
  (`0fd7e2829449b052abf0078666669302923d77af`), published as `@opencode/cli@2.0.23`; install it
  with `npm install -g @opencode/cli@2.0.23`.
- **A repository can be initialized in a project that has none.**
  `client.Vcs.InitializeRepositoryAsync(request)` (`vcs.init`) takes an optional `Provider` (git by
  default); a provider without initialization answers 501 `VcsInitNotSupportedError`. The route
  helper is `OpenCodeRoutes.Vcs.InitializeRepository`.
- **A missing directory is a typed 404.** Every location- and session-scoped route, and
  `experimental.generate.text`, `session.diff`, and `session.permission.list` by name, now declare
  `LocationNotFoundError` (`Location`, `Message`) for a location whose directory does not exist;
  before 2.0.23 the server failed such a request instead of answering it. A PTY connect refused
  with HTTP 404 carries no error body, so its `OpenCodeTransportException` now names both causes:
  the PTY session or the requested location does not exist.
- **`ServerInfo.Capabilities.PersistentPty`** says whether the server can run the
  persistent-terminal daemon: `false` on Windows, where `opencode-pty` ships no binaries. A server
  older than 2.0.23 leaves `Capabilities` null.

### 🔧 Changes

- **On Linux and macOS a standalone server runs in a session of its own, so a Ctrl+C or a hangup
  sent to the host's terminal no longer reaches it.** Behaviour change: the server used to stay in
  the host's process group, so a Ctrl+C the host itself handled still ended the server. It now
  ends through `DisposeAsync`, or, when the host dies without disposing, on the end-of-stream of
  its stdin lease.
- **Disposing a standalone server on Linux and macOS sends `SIGTERM` to the server's process group,
  and `SIGKILL` to whatever is left of the group after `GracefulShutdownTimeout`**, as upstream's own
  launcher does. Behaviour changes:
  - The pinned server answers the `SIGTERM` by exiting with code 130, where the stdin end-of-stream
    used to end it with code 0. Stdin now closes last.
  - Children the server started in its own process group end with it. Descendants that moved into a
    session of their own (the persistent-terminal daemon, PTY shells) are left to the server, as
    upstream leaves them; the forced tree kill used to end them too.
  - A server that already exited on its own with a non-zero code has the survivors of its group
    ended, and so does one whose exit status nobody could read (the host ignores `SIGCHLD` on
    Linux, or runs as pid 1); one that exited with code 0 or on a signal leaves them alone, as
    before.
- **A failed `StartAsync` on Linux and macOS ends the server the same way, with the configured
  grace**, so a canceled or timed-out start can take up to that grace plus 16 seconds to throw: at
  most 10 for the server's exit after `SIGKILL`, 1 for draining its output, and 5 for releasing the
  output readers. Behaviour change: it used to kill the server at once.
- **`StartAsync` on Linux and macOS sees the server's own exit without waiting for its output, and
  fails at once when the server closes its stdout before readiness.** A server that exits before
  readiness while a process it started still holds its stdout now fails the start within about a
  second instead of at the readiness timeout. Behaviour change in the message: a server ended by a
  signal before readiness reads "terminated on signal N", where it read "exited with code 128+N".
- **`OpenCodeServerOptions.WorkingDirectory` works on Linux with a C library older than glibc 2.29**
  (RHEL 8, Debian 10, Ubuntu 18.04): the server starts there through `/usr/bin/env -C`, which keeps
  it the same process, and a directory that does not exist fails the start as an exit with code
  125 whose stderr names it. A blank `WorkingDirectory` counts as none.
- **A standalone server's relative command path on Linux and macOS is resolved against the
  caller's current directory**, never against `WorkingDirectory`. Behaviour change: `Process`
  tried it against the application's own directory first, so a relative path that named a file
  there started that file even when the current directory held another.
- **`OPENCODE_PRINT_LOGS=1` in the host's environment hands the server the host's own stderr on
  Linux and macOS**, as upstream's launcher does; no stderr is then collected or quoted in a startup
  failure.
- **`EnsureAsync` reports the first contender failure without recruiting replacements.** While a
  failed contender's error stands, no further contender starts; the call throws it once no contender
  is live, and at the 120-second bound it throws that failure rather than the timeout. A recovery or
  replacement that ends the registered service clears it and releases the contenders it made moot.
  Behaviour change: a call that a recruited replacement contender used to rescue now throws the
  first contender's failure.
- **Persistent-terminal handoff and the sidecar clear are best-effort.** Preparing the handoff for
  a replacement never fails it: a failed or unusable ticket shuts the daemon's terminals down and
  publishes no ticket, and a sidecar that cannot be written leaves the replacement without one.
  `StopAsync` and the Ensure replacement end the service even when the sidecar cannot be removed.
  Behaviour change: `StopAsync` used to throw `OpenCodeServerException` when it could not remove
  the sidecar.
- **`StopAsync` escalates to the hard kill by the signalled process alone.** When the process
  survives the request to stop, the hard kill follows it even if its registration disappeared or
  another service registered meanwhile, as upstream's client does; a successor's registration is
  still never removed. Behaviour change: the stop used to end without the hard kill when the
  registration changed between the two signals.
- **Both packages declare `IsAotCompatible` on every modern target** (`net8.0` and later), not only
  `net10.0`, so trimming and native AOT analysis cover the `net8.0` and `net9.0` assets too.

### 🔒 Security

- **An unrecognized union value no longer prints a secret.** When a union's known shapes carry a
  masked member at any depth, its unknown arm — what a newer server's value decodes to — prints its
  preserved payload as `[REDACTED]` and keeps its marker: `UnknownCredentialValue`, `UnknownMcp`,
  `UnknownConfigEntry`, and the unknown arm of `McpRemoteConfigOauth`, `ConfigInfoFormatter`,
  `ConfigInfoLsp`, and `ConfigLspEntry`. `Payload` itself and serialization are unchanged.
- **Every user-configured header and environment map is masked in `ToString()`**, now including
  `ConfigProvider.Headers`, `ConfigModel.Headers`, `ConfigModelVariants.Headers`,
  `ConfigAgentRequest.Headers`, `ConfigFormatterEntry.Environment`, `ConfigLspServer.Env`, and
  `SessionEnvironmentRequest.Variables`.
- **The open provider options are masked in `ToString()`.** `ProviderSettings`,
  `ConfigProviderSettings`, `ModelSettings`, and `ConfigModelSettings` keep upstream's provider
  options, `apiKey` among them, in `AdditionalProperties`. A record printed that dictionary's type
  name, so no key was printed; it now prints `[REDACTED]` while the dictionary holds any member and
  empty while it holds none, so the value stays out if printing ever changes. The dictionary and
  serialization are unchanged.

### 🐛 Fixes

- **Disposing a standalone server on Linux and macOS no longer waits about 13 seconds when a
  process the server started keeps its output open.** The shutdown waits also waited for the
  server's redirected output to end, so a server that exited at once still ran out the 3-second
  grace and the 10-second forced-exit wait. Disposal now waits for the server's own exit and then
  for its process group; only an `OpenCodeServerOutput` collector waits for the output, for at most
  one second.
- **A standalone server on Linux and macOS starts with every signal at its default disposition and
  an empty signal mask**, as upstream's launcher starts it. It used to inherit a signal its host
  ignores (the .NET runtime ignores `SIGPIPE`, and a host started under `nohup` ignores `SIGINT`
  and `SIGHUP`).
- **Servers and background-service contenders started at the same time on macOS no longer receive
  each other's pipe ends.** macOS marks a new pipe close-on-exec only after creating it, so a spawn
  on another thread in between could inherit a server's stdin lease and keep it open after the host
  died. The SDK's spawns now take one lock around pipe creation and spawn, and on macOS the child
  receives no descriptor beyond its own three.
- **`OpenCodeServer.DisposeAsync` no longer throws when the forced tree kill is incomplete.** The
  runtime raises `AggregateException` when a process of the tree refuses the kill. That exception
  escaped disposal and skipped the rest of it: the bounded output drain, the release of the output
  readers (on Windows, a reader blocked on a pipe that a descendant still held was never
  cancelled), and the completion of the `OpenCodeServerOutput` collector. A failed `StartAsync`
  whose tree kill was incomplete threw that `AggregateException` instead of
  `OpenCodeServerException` or `OperationCanceledException`. Disposal and a failed start now
  continue past an incomplete kill and release everything.
- **A standalone server's stdout and stderr pipe handles are closed at disposal on Windows.**
  `Process` never closes a redirected stream that was read synchronously, so the handles waited for
  the finalizer; each output reader now closes its pipe as it ends, after a failed start too.
- **The `net472` and `netstandard2.0` Windows tree kill no longer leaves taskkill running.** A
  taskkill still running at its 10-second bound is now ended instead of left running.

### 📋 Important Notes

- **On Windows a standalone server is still started and ended through `Process`.** The session
  placement and the `SIGTERM`-then-`SIGKILL` ladder above apply to Linux and macOS only. On
  Windows, disposal closes the server's stdin, waits up to `GracefulShutdownTimeout`, and only then
  kills the server's process tree; a server that exits inside the grace leaves its descendants
  running.
- **`net8.0` and `net9.0` leave the packages, and `net11.0` joins, in an upcoming preview,** ahead of
  their end of support on 2026-11-10; this release still ships them. The packages will then target
  `netstandard2.0;net472;net10.0;net11.0`. An app on .NET 8 or 9 still installs through the
  `netstandard2.0` asset, but that asset is a compatibility asset, not a supported runtime: support
  and testing cover the targeted runtimes only, and .NET 5–7 are not supported. Plan the move to
  .NET 10.
- **Upstream's committed OpenAPI document is still stale at `v2.0.23`**
  ([anomalyco/opencode#53105](https://github.com/anomalyco/opencode/issues/53105)). It lacks
  `LocationNotFoundError`, and with it the 404 that 95 operations declare or widen for a missing
  location. This SDK's snapshot comes from upstream's own pinned generator, so a diff against that
  committed file shows them here; they are real and the server answers them.

## [0.9.0-preview.6] - 2026-10-04

**The pin moves to upstream release tag `v2.0.22`, and all 140 operations it exposes are
callable — 138 as generated HTTP calls, and the two terminal WebSocket connections through
hand-written transports.** Stored credentials can
now be listed and created, a session can be created as a linked child of another, a cancelled form
can tell the asker why, and a connection reports when it needs signing in again. The refresh
changes three shapes, so the breaking changes come first. Discovery, Ensure, and Stop now read the
CLI's service files exactly as the CLI does. `net8.0` and `net9.0` leave the packages in an
upcoming preview; see the important notes.

### 💥 Breaking changes

- **`CancelFormAsync` takes an optional `SessionFormCancelRequest` before `requestOptions`.** A call
  that passed `requestOptions` positionally names it now (`requestOptions: options`). The method is
  virtual, so overrides and mock setups move to the new signature, and an assembly compiled against
  `0.9.0-preview.5` must be rebuilt. The route helper `OpenCodeRoutes.Sessions.CancelForm` gained
  the same optional `request`.
- **`ProviderSettings.ChunkTimeout` and `ConfigProviderSettings.ChunkTimeout` are number-or-`false`
  unions, not `double?`.** Upstream accepts a number of milliseconds or `false` (no timeout), so a
  provider configured with `false` now decodes. Read `Kind`, then `Number` or `Boolean`; a value
  neither arm claims is kept as `Unknown`. `ChunkTimeout = 30000` becomes
  `ChunkTimeout = ProviderSettingsChunkTimeout.FromNumber(30000)` (`ConfigProviderSettingsChunkTimeout`
  on the config side), and `FromBoolean(false)` turns the timeout off; upstream refuses `true`.
- **`ConfigModel.Capabilities` is a `ConfigModelCapabilities`, not the catalog's
  `ModelCapabilities`.** It is the shape upstream's config accepts, and every member is optional:
  `Tools` is `bool?`, and `Input` and `Output` may be null. `ModelInfo.Capabilities` is unchanged.
- **`IConnectionInfo` has a `Status` member.** A type outside the SDK that implements the interface,
  such as a test fake, adds `ConnectionStatus? Status`; code that only reads connections is
  unaffected.

### ✨ Added

- **The accepted snapshot moved to upstream release tag `v2.0.22`**
  (`527f0b931d1f9b3ebd34e106c51b31ce5db5b075`), published as `@opencode/cli@2.0.22`; install it
  with `npm install -g @opencode/cli@2.0.22`.
- **Stored credentials can be listed and created.** `client.Credentials.ListCredentialsAsync()`
  (`credential.list`) returns every `CredentialEntry` (`Id`, `IntegrationId`, `Label`, `Active`,
  `Value`), and `CreateCredentialAsync(request)` (`credential.create`) stores one and returns it;
  an `Id` that already exists answers with the declared 409. They join activate, update, and
  remove. `Value` is a `CredentialKey` or a `CredentialOAuth`. Upstream returns secrets in clear
  text, so the API key (`CredentialKey.Key`) and the OAuth tokens (`CredentialOAuth.Access`,
  `Refresh`) are masked in `ToString()`.
- **`SessionCreateRequest.ParentId`** creates a linked child session at its parent's location; a
  missing parent answers with the declared 404 `SessionNotFoundError`.
- **A cancelled form can tell the asker why.** `CancelFormAsync(formId, new SessionFormCancelRequest
  { Message = "..." })` sends it, and `FormStateCancelled.Message` carries it back.
- **`HeaderTimeout`** on `ProviderSettings` and `ConfigProviderSettings` bounds the wait for a
  provider's response headers, as the same number-of-milliseconds-or-`false` union.
- **A connection reports when it needs attention.** `IConnectionInfo.Status` is a
  `ConnectionStatus` with the `Status`, a `Message`, and, when set, the `Url` to sign in again at.
- **`SessionStructuredError.Response`** carries the failed provider response's `Body`.

### 🐛 Fixes

- **`OpenCodeServer.DiscoverAsync`, `EnsureAsync`, and `StopAsync` decode the CLI's service files
  exactly as the CLI does.** A whole-valued number such as `8080.0` is an integer for the service
  config's `port` and for the pid in the registration and the info probe. Before, such a config was
  ignored, so `EnsureAsync` lost its `env` overlay, and such a registration was not found. A service
  config whose `disabled` member is not a boolean is no config at all, so its `env` overlay and the
  legacy config copy do not apply, as in the CLI. `disabled: true` itself does not stop
  `EnsureAsync`: only CLI commands read it, never the CLI's service operations.

### 📋 Important Notes

- **`net8.0` and `net9.0` leave the packages, and `net11.0` joins, in an upcoming preview,** ahead of
  their end of support on 2026-11-10. The packages will then target
  `netstandard2.0;net472;net10.0;net11.0`. An app on .NET 8 or 9 still installs through the
  `netstandard2.0` asset, but that asset is a compatibility asset, not a supported runtime: support
  and testing cover the targeted runtimes only, and .NET 5–7 are not supported. Plan the move to
  .NET 10.
- **Upstream's committed OpenAPI document is stale at `v2.0.22`**
  ([anomalyco/opencode#53105](https://github.com/anomalyco/opencode/issues/53105)). This SDK's
  snapshot comes from upstream's own pinned generator, so a diff against that committed file shows
  four more operations here; they are real and the server serves them.

## [0.9.0-preview.5] - 2026-09-29

**The packages are now `OpenCodeDotNet.Sdk` and `OpenCodeDotNet.Sdk.Extensions`, and the
repository lives in the `opencode-dotnet` organization.** The API is unchanged: only the
`PackageReference` changes. The release also fixes thread-pool starvation from standalone servers
on Windows.

### 💥 Breaking changes

- **The packages are now `OpenCodeDotNet.Sdk` and `OpenCodeDotNet.Sdk.Extensions`.** Change the
  `PackageReference` from `OpenCodeAI.Sdk` and `OpenCodeAI.Sdk.Extensions`; the earlier ids receive
  no further versions. Assemblies and namespaces are still `OpenCode.Sdk`, so no source changes.
- **The repository moved to
  [opencode-dotnet/opencode-sdk-dotnet](https://github.com/opencode-dotnet/opencode-sdk-dotnet).**
  GitHub redirects the old links. The nightly feed moved with it to
  `https://nuget.pkg.github.com/opencode-dotnet/index.json`; the earlier feed keeps its existing
  `OpenCodeAI.*` nightlies and receives no new ones.

### 🐛 Fixes

- **A standalone server no longer holds two thread-pool threads on Windows.** `Process` creates
  the redirected stdout and stderr pipes synchronous there before .NET 11, and its event readers
  blocked a pool thread on each for the server's whole life; with a few servers in one process the
  pool starved, and unrelated async work stalled for seconds. The launcher now reads both streams
  on dedicated threads on Windows, and disposal ends them: it waits for end-of-stream inside the
  drain bound and cancels a read still blocked, so no reader outlives the server handle. That also
  keeps the last lines of an exited child from missing the `OpenCodeServerOutput` snapshot under
  load. Linux and macOS are unchanged.

## [0.9.0-preview.4] - 2026-09-27

**Every operation upstream exposes is now callable: 100% coverage of the protocol surface.** At
upstream release tag `v2.0.18`, all 138 operations in the pinned document are usable — 136
generated and the two terminal WebSocket transports — and none is declined. `0.9.0-preview.3`
generated 130 and declined 4. `config.get`, `experimental.migration.v1.status`, `fs.read`, and
`experimental.fs.write` are now generated, and the refresh to `v2.0.18` adds pairing.

### ✨ Added

- **The accepted snapshot moved to upstream release tag `v2.0.18`**
  (`cd9a14a6b688d4021bee381dfd39d2cef9c0f862`), published as `@opencode/cli@2.0.18`; install it
  with `npm install -g @opencode/cli@2.0.18`. It adds two operations and moves none, so all 138
  are usable: 136 generated and the two WebSocket transports.
- **Pairing.** `client.Server.CreatePairingCodeAsync()` (`server.pair`) issues a short-lived,
  single-use code; `client.Server.RedeemPairingCodeAsync(code)` (`server.connect`) exchanges it,
  with no other credential, for a session token the server accepts anywhere the password is, so
  another client connects with `Password = token` without ever holding the server password. The
  token is masked in `PairingSession.ToString()`, and so is the code in `PairingCode.ToString()`.
- **`ShellInfo.Signal`** names the signal that ended a killed command.
- **The configuration can be read back.** `client.Config.GetConfigAsync()` (`config.get`) returns
  the configuration documents and discovery sources for the location, from lowest to highest
  priority: a `ConfigDocument` carries its `ConfigInfo`, a `ConfigDirectory` the directory it came
  from. `Experimental.UpdateConfigAsync` writes the global file and the server reloads it
  asynchronously, publishing `config.updated` once the loaded configuration changes, so read after
  that event.
- **`client.Experimental.GetMigrationV1StatusAsync()`** (`experimental.migration.v1.status`)
  returns the V1 session-history migration status: `ExperimentalMigrationV1StatusIdle`
  (`Required` or `Completed`), `…Running` with its progress, or `…Error`.
- **Files can be written.** `client.Experimental.WriteFileAsync(new ExperimentalFsWriteRequest { Path = … }, stream)`
  (`experimental.fs.write`) sends the stream as the raw body and returns the resolved absolute path
  as `File.Path`. The SDK reads the stream once from its current position to its end and never
  disposes or rewinds it; a slow upload stays alive while it keeps moving. Upstream does not confine
  the target to the location, so a relative `Path` resolves against it and an absolute one does not.
- **Files can be read.** `client.FileSystem.ReadFileAsync(new FsReadRequest { Path = … })`
  (`fs.read`) returns the file's bytes as `Content` (`ReadOnlyMemory<byte>`) and the MIME type the
  server declared as `ContentType`. `Path` is relative to the location and may be nested; the SDK
  escapes each segment as upstream's own client does and refuses `.` and `..` segments. A missing
  file is the declared 404 `FileNotFoundError`; a path that leaves the location is refused by the
  server.
- **Unions of objects without a discriminator are typed.** A structural union carrier whose object
  members share the JSON object token now selects a member the way upstream's decoder does: the
  first member in declaration order whose required keys are present and whose literal-constrained
  properties hold admitted values, then that member's full parse. A value no member claims is kept
  as the carrier's raw `Unknown`. The configuration's reference entries (`ConfigReferenceEntry`),
  language-server entries (`ConfigLspEntry`), and the migration status use it.
- **Operation members name their OpenAPI operation.** Each generated operation method, each
  `Enumerate*Async` companion, and each hand-written PTY and persistent-PTY door carries an XML
  `<remarks>` with its operation id, HTTP method, and route, for example
  `Operation session.permission.create: POST /api/session/{sessionID}/permission`.

### 🔧 Changes

- **A 401 now carries the typed `UnauthorizedError`.** From 2.0.18 the server answers a missing or
  refused credential with the error body its document declares, where it used to send an empty
  one, so `Error` is `UnauthorizedError` instead of `null`. Nothing changed in the SDK; the errors
  guide now says so.

### 🐛 Fixes

- **A logged response no longer prints the raw error body.** Every response envelope's
  `ToString()` printed `RawBody`, so logging a failed response wrote whatever the server echoed into
  its error body, request data included. The printed form now keeps `Status`, `IsError`, and the
  typed `Error`; `RawBody` itself is unchanged and still carries the body.
- **`OpenCodeServer.EnsureAsync` no longer holds a thread-pool thread per contender on Windows.**
  The contender's standard error was an anonymous pipe, which Windows reads synchronously, so every
  contender Ensure started kept one pool thread blocked for as long as its standard error stayed
  open — for the elected service, its whole life — and ten concurrent callers stalled the pool for
  seconds. The pipe's read end is now overlapped, as in .NET 11's `Process` and libuv.

### 📚 Documentation

- **The README is a front page.** It opens with the status and a capability table that maps each
  area of the API to its `client.*` entry point and guide page. The quick start opens with a table
  for choosing a connection mode, and the known issues keep only what a consumer meets. Every link
  is absolute, so the page also works on NuGet.org.
- **The connection guide shows how to pair another client** (`CreatePairingCodeAsync`, then
  `RedeemPairingCodeAsync`, then `Password = token`), and it lists the launcher's `Output` option.
- **A CLI-started server's session log is the marker alone, followed as well as replayed.** The
  streaming guide and the README's known issues said live `Follow = True` delivery was unaffected
  by persistence; it is not. The server's live tail re-reads the same persisted store as a replay,
  so against a server the distributed `opencode` CLI started, `GetLogAsync` delivers one
  `EventLogSynced` and no durable event either way. Both now say so and point live session activity
  to `client.Events.SubscribeAsync`, filtered by session id. Observed on `@opencode/cli@2.0.15`.
- **A refused worktree removal can be partial.** The errors guide said a 400 `WorktreeError` removed
  nothing. With `ForceRequired` false, git can already have deleted the worktree's files and its
  git metadata while the directory and the inventory row remain, and a retry then answers
  `Worktree directory unavailable` with `ForceRequired` null. The guide names the cause observed on
  Windows at the pin — the server keeps the worktree's location alive, and that location's local MCP
  servers run inside the worktree — and shows how to prevent it (`Debug.EvictLocationAsync` before
  the removal) and how to recover (evict, delete the directory, `RefreshWorktreesAsync`).
- **An empty VCS summary is explained.** Getting started says that `Vcs.GetVcsAsync` for a
  directory the server has not served before can answer with a `null` `Provider` and `null` branch
  values until that location's plugins settle, that the caller waits for a non-null `Provider`,
  and that a `null` `Branch.Current` with a provider set is a detached HEAD.
- **Getting started shows a prompt answered in a console app**: `PromptAsync`, then
  `Experimental.WaitForSessionAsync` under a caller-owned deadline, then `EnumerateMessagesAsync`.
- **Getting started has a .NET Framework section.** A `net472` project needs
  `<LangVersion>10.0</LangVersion>` — the SDK's public surface needs C# 9 and the console template's
  implicit usings C# 10 — and no extra package.
- **The requests guide explains the members the schema does not**: session permission rules
  (an update replaces the whole ruleset; create, list, and reply; how upstream evaluates), a
  worktree's `Directory` as the parent and `Name` as the child, and a shell `Timeout` in
  milliseconds with `0` or unset meaning none.
- **The README no longer states a test count by hand**; the test badges carry it. A tools test now
  fails when the README's guide table and the pages under `docs/guide` differ.

## [0.9.0-preview.3] - 2026-09-24

The launcher is complete: `OpenCodeServer.EnsureAsync` joins discovery and stop, so the three
connection modes upstream's CLI offers are all in the SDK. The SDK follows upstream release tag
`v2.0.15` instead of `v2.0.11`, which makes two wire members required — the breaking changes
come first — and adds session metadata updates. Generated models stop printing secrets, and the
background-service door reads registrations the way upstream does in every case the fixes below
list.

### 💥 Breaking changes

- **The accepted snapshot moved to upstream release tag `v2.0.15`**
  (`6f3639d82ed0760091792189b78f8eeb44f699b1`), which published as `@opencode/cli@2.0.15`; install
  it with `npm install -g @opencode/cli@2.0.15`. No operation was added, removed, or moved since
  `v2.0.11`. The Restore patch for upstream's lost SSE payload schemas
  ([anomalyco/opencode#44911](https://github.com/anomalyco/opencode/issues/44911)) is still
  required at this tag and applies unchanged.
- **`ConnectionCredentialInfo.Method` is required.** A stored connection credential now says how it
  was obtained, `ConnectionCredentialInfoMethod.Key` or `ConnectionCredentialInfoMethod.Oauth`; a
  credential from a server that omits it no longer deserializes.
- **`ProjectTime.Active` is required.** It is the project's most recent activity, in the same unit
  as `Created` and `Updated`, and upstream now orders the project list by it; a project from a
  server that omits it no longer deserializes.

### ✨ Added

- **Session metadata updates.** `SessionUpdateRequest.Metadata` replaces a session's metadata as a
  whole — keys the update leaves out are removed, not kept — and the server logs the replacement as
  the durable `SessionMetadataUpdated` event (`session.metadata.updated`), which the event stream
  and the session log deliver.

- **`OpenCodeServer.EnsureAsync`**, the managed-service election beside discovery and stop.
  It reuses a ready compatible daemon, replaces a version-mismatched one according to
  `OpenCodeServerEnsureOptions.VersionPolicy` (`Ignore`, `Replace`, `Error`), and otherwise
  spawns detached contenders until one registers or the 120-second wall-clock bound expires.
  The persistent-terminal handoff sidecar travels with replacement, and `OnStart` fires at
  most once before a new service process is spawned.

### 🔒 Security

- **Generated models no longer print secrets.** A model's `ToString()` — string interpolation,
  logging templates, the debugger — printed every member, so logging an `McpOAuthConfig`, or the
  `McpRemoteConfig` holding it, wrote its `ClientSecret`. Members upstream's own recorder redacts
  (`client_secret`, `password`, `api_key`, `token`, …) now print `[REDACTED]`, as do
  `IntegrationConnectKeyRequest.Key` and the user-configured header and environment maps; an
  absent secret prints empty, and every other member prints as before. Equality and JSON are
  unchanged.

### 🐛 Fixes

- **Discovery reaches a background service bound to every interface.** A daemon started with
  `hostname: 0.0.0.0` (or `::`) registers that address, which .NET refuses as a connect target, so
  `DiscoverAsync` returned null and `StopAsync` could not ask it to shut its terminals down while
  the CLI worked. The connect target is now the same family's loopback, as Bun's is; the
  registered URL still identifies the daemon.
- **A registration file that is not valid UTF-8 reads as absent.** `DiscoverAsync` and
  `StopAsync` threw `InvalidOperationException` for a registration holding an invalid UTF-8
  string, outside their documented failures; such a file is now no registration, like any other
  undecodable one. A byte-order mark at the start of a registration or service-config file is
  skipped, as the CLI skips it.
- **A whitespace password is sent as written.** `OpenCodeClientOptions.Password` refused an empty
  or whitespace value at client construction, and a background-service registration carrying a
  whitespace password read as passwordless, so discovery skipped a daemon the CLI reached. The
  CLI's server runs with any configured password, whitespace included; only a null password now
  means "no credential", as in the pinned client.
- **Reading a registration no longer blocks its removal on Windows.** A service that exits removes
  its registration while clients may be reading it; the SDK now opens the file with delete sharing,
  as libuv does, so the removal never fails on the SDK's account.
- **`OpenCodeServer.StopAsync` treats a zombie as stopped on Linux.** A service process that had
  exited but was not yet reaped by its parent kept its start time readable, so the stop read it
  as still running, sent the kill rung, and failed with "still running after the kill rung",
  leaving the registration behind. The identity read now checks the process state and treats a
  zombie as gone, the way macOS already did.
- **Structural union values print themselves.** `ToString()` on `FormValue`, `FormWhenValue`,
  `McpRemoteConfigOauth`, and `ProviderSettingsTimeout` threw `InvalidOperationException`
  ("The structural value does not contain the requested arm."): a record's compiler-synthesized
  `PrintMembers` read every arm, and an inactive arm throws by design. Each union now prints its
  kind and its active arm only — `ProviderSettingsTimeout { Kind = Number, Number = 30000 }`,
  `FormValue { Kind = TextList, TextList = [a, b] }` — with numbers in the invariant culture and an
  `Unknown` arm as its raw JSON. String interpolation, logging templates, and the debugger were
  the paths that hit this; equality and JSON were never affected.

## [0.9.0-preview.2] - 2026-09-21

Three things changed since `0.9.0-preview.1`. The SDK follows upstream release tag `v2.0.11`
instead of `v2.0.8`, which reshapes the provider and model catalog entries. Public names no
longer carry the HTTP method, so a wave of methods and types is renamed; the breaking changes
come first, each with what to change. And `OpenCodeServer` gained `StopAsync`, the stop door
beside discovery.

### 💥 Breaking changes

- **The accepted snapshot moved to upstream release tag `v2.0.11`**
  (`9eb6902aaf3c35ce985b67c605a775992249066b`), which published as `@opencode/cli@2.0.11`; install
  it with `npm install -g @opencode/cli@2.0.11`. No operation was added, removed, or moved since
  `v2.0.8`; the change is in the provider and model catalog entries. The Restore patch for
  upstream's lost SSE payload schemas
  ([anomalyco/opencode#44911](https://github.com/anomalyco/opencode/issues/44911)) is still
  required at this tag and applies unchanged.
- **Provider and model settings are typed open objects.** `ProviderInfo.Settings` is a
  `ProviderSettings` (`Timeout`, `ChunkTimeout`, `Compaction`, `Transport`) and `ModelInfo.Settings`
  and `ModelVariant.Settings` are a `ModelSettings` (`Compaction`) instead of
  `IReadOnlyDictionary<string, JsonElement>`; `ProviderRequestOptions.Settings` is the same
  `ProviderSettings`. Every other member the server puts in `settings` — `baseURL`, `apiKey`,
  `region`, and whatever a provider adds — is in `AdditionalProperties`, an
  `IReadOnlyDictionary<string, JsonElement>` keyed by wire name that is empty, never null, when
  the body carried none; read it the way upstream does, by checking the `ValueKind` you expect.
  `Compaction` and `Transport` are removed from `ModelInfo` and `ProviderInfo` themselves: upstream
  moved them under `settings`. `Timeout` is a `ProviderSettingsTimeout` structural union
  (`Number`, `Boolean` for the wire's `false`, `Unknown`).
- **The compaction union changed its arms and marker.** `IProviderCompaction` now dispatches on
  `type` with `ProviderCompactionSummary` (`summary`) and `ProviderCompactionNative` (`native`);
  `ProviderCompactionLocal`, `ProviderCompactionProvider`, its `Threshold`, and the `mode` marker are
  gone with upstream's schema.
- **The HTTP method is no longer part of any name.** An operation's verb is its closing identifier
  segment when that segment is one of `create`, `get`, `list`, `remove`, `rename`, `timeout`, or
  `update`; a `GET` without one is a read and names `Get<Subject>Async`; every other operation is
  named by a reviewed, reason-bearing curation row, and the generator refuses to fall back to
  `Post…`, `Put…`, `Patch…`, or `Delete…`. Three consequences reach your code:
  - **Every `Post…`/`Put…`/`Patch…`/`Delete…` method is renamed.** On `SessionClient`:
    `PostPromptAsync` → `PromptAsync`, `PostGenerateAsync` → `GenerateTextAsync`,
    `PostCommandAsync` → `RunCommandAsync`, `PostShellAsync` → `RunShellCommandAsync`,
    `PostCompactAsync` → `CompactAsync`, `PostForkAsync` → `ForkAsync`, `PostMoveAsync` →
    `MoveAsync`, `PostInterruptAsync` → `InterruptAsync`, `PostBackgroundAsync` →
    `MoveToolsToBackgroundAsync`, `PostViewAsync` → `MarkViewedAsync`, `PostSyntheticAsync` →
    `AddSyntheticMessageAsync`, `PostSwitchAgentAsync` → `SwitchAgentAsync`,
    `PostSwitchModelAsync` → `SwitchModelAsync`, `PutEnvironmentAsync` → `SetEnvironmentAsync`,
    `PostFormReplyAsync` → `ReplyToFormAsync`, `DeleteFormCancelAsync` → `CancelFormAsync`,
    `PostPermissionReplyAsync` → `ReplyToPermissionAsync`, `DeleteInboxCancelAsync` →
    `CancelInboxAsync`, `PostRevertStageAsync` → `StageRevertAsync`, `PostRevertCommitAsync` →
    `CommitRevertAsync`, `DeleteRevertClearAsync` → `ClearRevertAsync`. On `IntegrationClient`:
    `PostConnectKeyAsync` → `ConnectWithKeyAsync`, `PostOauthConnectAsync` →
    `BeginOauthConnectionAsync`, `PostOauthCompleteAsync` → `CompleteOauthConnectionAsync`,
    `DeleteOauthCancelAsync` → `CancelOauthConnectionAsync`, `PostCommandConnectAsync` →
    `BeginCommandConnectionAsync`, `DeleteCommandCancelAsync` → `CancelCommandConnectionAsync`.
    On `ExperimentalClient`: `PostSessionImportAsync` → `ImportSessionAsync`,
    `PostSessionSkillAsync` → `ActivateSessionSkillAsync`, `PostSessionWaitAsync` →
    `WaitForSessionAsync`, `PutSessionInstructionsEntryAsync` →
    `SetSessionInstructionsEntryAsync`. On `PtyClient`: `PutUpdateAsync` → `UpdateAsync`.
  - **A handle names itself, not its family.** A handle client's own read, update, and remove
    drop the family word: `SessionClient.GetSessionAsync` / `UpdateSessionAsync` /
    `RemoveSessionAsync` → `GetAsync` / `UpdateAsync` / `RemoveAsync`; likewise
    `ShellClient.GetShellAsync` / `RemoveShellAsync`, `IntegrationClient.GetIntegrationAsync`,
    `PtyClient.GetPtyAsync` / `RemovePtyAsync`, and `PersistentPtyClient.GetPersistentPtyAsync` /
    `UpdatePersistentPtyAsync` / `RemovePersistentPtyAsync`. Collection clients are unchanged
    (`Sessions.ListSessionsAsync`, `Sessions.CreateSessionAsync`).
  - **Request, response, and payload types drop the verb the same way.** Every
    `…PostRequest`, `…PutRequest`, `…PatchRequest`, `…DeleteRequest` and the matching
    `…Response` / `…Data` type loses the method word: `SessionPromptPostRequest` →
    `SessionPromptRequest`, `SessionPromptPostResponse` → `SessionPromptResponse`,
    `SessionUpdatePatchRequest` → `SessionUpdateRequest`, `PtyUpdatePutResponse` →
    `PtyUpdateResponse`, `LocationReloadPostResponse` → `LocationReloadResponse`,
    `PluginCheckPostRequest` → `PluginCheckRequest`, `SessionGeneratePostData` →
    `SessionGenerateData`, and so on for all 104 such types; grammar verbs keep their word
    (`SessionListResponse`, `SessionRemoveResponse`). Two names needed more than the rule: the
    body `session.interrupt` answers with was upstream's `SessionInterruptResponse` and is now
    `SessionInterruptOutcome`, so that the operation's own response record can carry
    `SessionInterruptResponse` (`response.Interrupt.Interrupted` reads as before); and the three
    update responses whose payload was mislabelled `Update` now name their subject —
    `PtyUpdateResponse.Pty`, `PersistentPtyUpdateResponse.PersistentPty`,
    `ProjectUpdateResponse.Project`. The route builders in `OpenCodeRoutes` follow their methods
    (`Sessions.PostPrompt(id)` → `Sessions.Prompt(id)`, `Sessions.GetSession(id)` →
    `Sessions.Get(id)`, `…Template` constants alike).
- **`McpProtocol.Value20260728` is `McpProtocol.Revision20260728`.** The wire value `2026-07-28`
  is unchanged; a reviewed enum member-name row names the member.

### ✨ Added

- **`OpenCodeServer.StopAsync` stops the registered background service** — opencode's
  `service stop` for your code, shaped by `OpenCodeServerStopOptions` (`Channel`,
  `RegistrationFilePath`, `InstalledVersion`). It resolves the registration the way discovery does,
  asks a ready daemon to shut its persistent terminals down, clears the handoff sidecar, ends the
  registered process with the CLI's own ladder (`SIGTERM`, about five seconds, then `SIGKILL`;
  hard kills on Windows), and removes the registration once the process is gone. The registration
  is re-read before every signal and the process is identified by pid and start time, so a service
  that re-registered under the file or a pid the operating system reused is never signalled. A
  missing registration completes successfully; a process still running after the hard kill throws
  `OpenCodeServerException` and leaves the registration in place. Disposing a discovered handle
  still stops nothing.

### 🔧 Changes

- **Discovery answers a stale registration at once on Windows.** `OpenCodeServer.DiscoverAsync`
  probes the registered loopback address without SYN retransmission, so a registration whose
  daemon is gone returns null in milliseconds instead of waiting out the two-second bound, and a
  refused port is classified as "no service" rather than a timeout on every operating system and
  target framework — the classification the Ensure door will rely on. The probe also never routes
  a loopback request through a proxy: `HTTP_PROXY` without `NO_PROXY` no longer hides a running
  service. Nothing changes for the SDK's other calls.

## [0.9.0-preview.1] - 2026-09-18

Two things changed since `0.8.0-preview.2`. The SDK now follows upstream release tag `v2.0.8`
instead of `v2.0.2`: upstream renamed, moved, or removed many operations in between, and the SDK
keeps no compatibility layer, so the breaking changes come first, each with what to change. And
`OpenCodeServer` gained background-service discovery, opencode's third connection mode.

### 💥 Breaking changes

- **The accepted snapshot moved to upstream release tag `v2.0.8`**
  (`7673ed6bd6547ee0dcb81aab55f1392fb751d652`), which published as `@opencode/cli@2.0.8`; install
  it with `npm install -g @opencode/cli@2.0.8`. Operation identities lost their `v2.` prefix
  upstream (`session.diff`, `event.subscribe`); routes did not change for that reason. 130 of
  the document's 136 operations are generated and two more are hand-written
  WebSocket transports. The Restore patch that repairs upstream's lost SSE payload schemas
  ([anomalyco/opencode#44911](https://github.com/anomalyco/opencode/issues/44911)) is still
  required at this tag and applies unchanged.
- **Health became server info.** `OpenCodeClient.GetHealthAsync`, `ServerClient.GetServerAsync`,
  `HealthResponse`, `Health`, and `ServerResponse` are removed with upstream's `/api/health` and
  `/api/server`. Call `client.Server.GetInfoAsync()` (`GET /api/info`) and read
  `ServerInfoResponse.ServerInfo`: `Version`, `Pid`, `Urls`, and `Paths.Tmp`, the server's
  temporary directory. There is no `Healthy` member; an info call that answers is the health
  signal, and it throws like any other call when the server does not. Background-service discovery
  probes the same door, and a registered daemon of an older 2.x release, which still serves
  `/api/status`, is reported as present but incompatible.
- **Location targeting is directory-only.** `LocationSelector.Workspace` and
  `SessionListRequest.Workspace` are removed: upstream dropped the `location[workspace]` query and
  the `x-opencode-workspace` header. The `Location` a location-scoped response carries is now
  `LocationPublicRef` with `Directory` alone, and `location.get` answers `LocationPublicInfo`;
  `LocationInfo` and `LocationInfoProject` are removed. The `client.Workspaces` family
  (`WorkspacesClient`) is removed with upstream's workspace routes.
- **Operations upstream marked experimental moved to `client.Experimental`**, as flat methods that
  take their ids explicitly:
  - `SessionClient.GetExportAsync` → `Experimental.GetSessionExportAsync` (payload member
    `SessionExport`), `SessionsClient.PostImportAsync` → `Experimental.PostSessionImportAsync`,
    `SessionsClient.GetStatsAsync` → `Experimental.GetSessionStatsAsync`.
  - `SessionClient.PostSkillAsync` / `PostWaitAsync` → `Experimental.PostSessionSkillAsync` /
    `PostSessionWaitAsync`, and the three `…InstructionsEntryAsync` methods →
    `Experimental.ListSessionInstructionsEntryAsync`, `PutSessionInstructionsEntryAsync`, and
    `RemoveSessionInstructionsEntryAsync`.
  - The `McpServerClient` handle and `McpServersClient.GetMcpServerClient` are removed; adding,
    connecting, disconnecting, and removing a server are `Experimental.AddMcpServerAsync`,
    `ConnectMcpServerAsync`, `DisconnectMcpServerAsync`, and `RemoveMcpServerAsync`.
    `McpServersClient` keeps the list and the resource catalog.
  - `client.Generation.GenerateTextAsync` → `Experimental.GenerateTextAsync`; `GenerationClient`
    is removed.
- **Configuration preferences are gone upstream.** `ConfigClient.GetPreferencesAsync`,
  `PatchUpdatePreferencesAsync`, and the `ConfigPreferences*` and `ConfigWebSearchInfo` models are
  removed with `/api/config/preferences`. Write through `Experimental.UpdateConfigAsync`, whose
  `Shell` is required and nullable: a string sets the shell, `null` clears it, and there is no
  "leave it unchanged" form any more. **There is no configuration read at this pin**: upstream
  folded it into `config.get`, which this SDK does not generate yet
  ([coverage](README.md#-api-coverage)). `ConfigClient` keeps `GetShellsAsync`.
- **Session operations that changed shape.** `RenameSessionAsync` and `PutPermissionRulesAsync` →
  `SessionClient.UpdateSessionAsync` (`Title`, `Permissions`). `PostInboxQueueAsync` and
  `PostInboxSteerAsync` → `UpdateInboxAsync` with the delivery to switch to. `GetFormStateAsync`
  is removed; the state is `GetFormAsync(…).Form.State` on the new `FormDetail`.
  `PostFormCancelAsync` → `DeleteFormCancelAsync` and `PostRevertClearAsync` →
  `DeleteRevertClearAsync` (upstream changed the method). The fork boundary union
  (`ISessionForkRequestBoundary`, `SessionForkRequestBoundaryBefore`, `…Through`) is removed; set
  `SessionForkPostRequest.Before`. `SessionInterruptPostRequest.Continue` → `Resume`.
  `MessageListRequest`, `MessageListResponse`, and `MessageListRequestType` →
  `SessionMessageListRequest`, `SessionMessageListResponse`, and `SessionMessageListRequestType`.
- **Events.** `session.permissions.updated` is now `session.permissions`:
  `SessionPermissionsUpdated` and its `Data` and `Durable` companions → `SessionPermissions`,
  `SessionPermissionsData`, and `SessionPermissionsDurable`. `catalog.updated` (`CatalogUpdated`)
  is removed; `provider.updated` and `model.updated` arrive as `ProviderUpdated` and `ModelUpdated`.
- **Worktrees name their project.** Every worktree request requires `ProjectId`, and the requests
  no longer take a `Location`; `WorktreeCreateRequest.Strategy` is removed.
- **Smaller renames and removals.** `VcsClient.GetBranchesAsync` → `ListBranchesAsync`
  (`VcsBranchListRequest` / `VcsBranchListResponse`). `FormsClient.ListRequestsAsync` →
  `ListFormsAsync`, and its payload `Requests` → `Forms`. `ProjectsClient.GetCurrentAsync` is
  removed; the current project arrives on `client.Location.GetLocationAsync()`.
  `ShellClient.TimeoutShellAsync` is removed. `ShellCreateRequest.Timeout` is optional
  (`long` → `long?`). `SkillInfo.Location` and `Slash` → `Path`. An unknown
  integration now throws the declared 404 instead of returning null.
- **`PluginsClient.AwaitPluginActivationAsync` is removed and has no replacement**: the pin exposes
  no HTTP activation barrier. When your code needs a particular plugin, provider, or model, wait
  for that identity under your own cancellation deadline; an empty list does not prove absence.

### ✨ New features

- **Background-service discovery.** `OpenCodeServer.DiscoverAsync` opens opencode's third
  connection mode: it finds the background service the opencode CLI registers for every client on
  the machine, through the CLI's own rules — the registration file resolved by service channel
  under the XDG state root (or named directly), the CLI's one-time copy of an older hashed
  registration filename, a strict decode of the registration, and an authenticated `/api/info`
  probe under a two-second bound whose pid must match. A ready daemon comes back as a
  **non-owning** `OpenCodeServer`: the new `OwnsProcess` member is false, `DisposeAsync` is a
  no-op, and `CreateClient()` binds a client to the daemon exactly as it does to a started server.
  Everything unusable — no registration, an undecodable or passwordless one, a daemon still
  starting or failed, a probe timeout, a version other than the expected one — is a null answer,
  never a half-usable handle. `OpenCodeServerDiscoverOptions` carries `Channel`,
  `RegistrationFilePath`, `ExpectedVersion`, and `InstalledVersion`, validated at the call. This is
  the one SDK door that reads the environment, and it reads exactly `XDG_STATE_HOME`,
  `XDG_CONFIG_HOME`, `OPENCODE_CONFIG_DIR`, and the user profile (`USERPROFILE` first on Windows,
  `HOME` elsewhere) — never a credential. The behaviour is the pinned CLI's, source-watched at the
  accepted commit (ADR-0025); live tests prove it against the pin's own `serve --service` daemon on
  every target-framework leg, from an isolated process whose environment the test owns. Ensuring and
  stopping the service follow as their own slices.
- **Turn diffs.** `SessionClient.GetDiffAsync` binds `session.diff`: the structured per-file diffs of the files one turn changed, where a turn runs from the first
  prompt after the session was last idle to its next idle marker. `SessionDiffRequest` carries the
  optional `From` and `To` user-message anchors and the `Context` line count, and
  `SessionDiffResponse.Diffs` is the same `FileDiffInfo` list the VCS diff returns. A live test
  proves the declared 200, 400, and 404 arms against the pinned server.
- **The idle turn marker is a typed message.** `SessionMessageIdle` joins the message union with
  its `Outcome` (`Succeeded`, `Failed`, or `Interrupted`). The server writes one at the end of
  every turn; on `0.8.0-preview.2` those entries surfaced as `UnknownSessionMessageInfo`.
- **Reloading every loaded location.** `client.ReloadLocationsAsync()` binds `location.reload`:
  the server shuts down and rebuilds every loaded location, cancels
  pending permissions and forms, lets running sessions continue with fresh services at their next
  step boundary, and answers once every replacement build settles. The new `location.shutdown`
  event (`LocationShutdown`) arrives on the event stream for each location it tears down, so a
  consumer can revalidate its reads. The declared `ServiceUnavailableError` is its one typed
  failure.
- **Smaller additions.** `SessionStepStartedData.Started` carries the step's
  request dispatch time, before the provider answers, and every `Form*Field` has an optional
  `Hidden` that skips the interactive prompt when a default exists.

### 🔧 Changes

- **The version line is `0.9.0`.** Discovery is the M4 milestone's background-service boundary;
  nightlies are `0.9.0-nightly.*`.
- **`OpenCodeServerException` now covers every local-server door.** Discovery throws it for one
  cause only — an XDG variable unset with no user home to fall back to — beside the launcher's
  start, readiness, and stop failures. The class summary and the errors guide say so.
- **`OpenCodeServer` is the home of every local-server handle** (ADR-0024): its summary no longer
  promises that it never attaches to another server, because a discovered handle is exactly that,
  with its ownership visible through `OwnsProcess`.
- **The published opencode build is tested now, not only the pinned source.** The live-test fixture
  takes a new optional `OPENCODE_SDK_TESTS_SERVER_COMMAND` (`|`-separated, e.g. `opencode|serve`):
  it replaces the command the fixture starts and changes nothing else, so the whole suite can be
  pointed at any build of the same server — the executable is resolved from `PATH` by the SDK's own
  launcher. The new [Consumer leg](.github/workflows/consumer-leg.yml) workflow — on demand and
  weekly, Linux and Windows — uses it to run that suite against
  `npm install -g @opencode/cli` at the version [`spec/SNAPSHOT.md`](spec/SNAPSHOT.md) pins, after
  asserting the version the installed command reports.

## [0.8.0-preview.2] - 2026-09-12

The second preview, built against upstream release tag `v2.0.2`. The breaking changes come
first, each with what to change; nothing else needs action to upgrade from `0.8.0-preview.1`.

### 💥 Breaking changes

- **A request property the API document declares both optional and nullable is now
  `Optional<T?>`** instead of `T?` — 39 properties across 21 operations, one of them the shared
  `ToolFileContent.Name`. The wrapper is what tells "leave this alone" apart from "send an
  explicit null": an unassigned member is absent and is not written at all, `= null` and
  `Optional<T?>.Null` both write JSON null, and a value assigns through an implicit conversion, so
  `Title = "Fix the build"` keeps compiling unchanged. Reading one back goes through `IsSet` and
  `Value`, and `= default` returns a member to absent. Response-only schemas are untouched;
  `ToolFileContent` is the one schema shared both ways, so its `Name` carries the wrapper on the
  read side too and is read through `.Value`. The new [Requests](docs/guide/requests.md) guide page
  covers it.
- **The accepted snapshot moved to upstream release tag `v2.0.2`**
  (`ea5ae2329569e4fbf063be451480b58e29de6816`), which published as `@opencode/cli@2.0.2`. Install
  that release (`npm install -g @opencode/cli@2.0.2`); the `@opencode-ai/cli` scope that the
  previous README named is frozen at an August build that predates the worktree route shape and the
  whole persistent-PTY family this SDK generates.
- **`v2.session.messageUpdate` is gone** because upstream removed the message content mutation API
  ([anomalyco/opencode#48043](https://github.com/anomalyco/opencode/pull/48043)).
  `SessionClient.PatchMessageUpdateAsync`, `SessionMessageUpdatePatchRequest`,
  `SessionMessageUpdatePatchResponse`, and the route constants are removed, and with them the
  `ISessionMessageAssistant` union and its `UnknownSessionMessageAssistant` carrier, which only that
  request body referenced. Assistant content records keep `ISessionMessageAssistantContent`.
- **`session.message.content.updated` left the live event union.** `SessionMessageContentUpdated`
  no longer implements `IEvent`; it remains a durable session-log item.

### ✨ New features

- **A preference can be cleared, not just overwritten.** `PatchUpdatePreferencesAsync` sends an
  explicit JSON null for `Shell` or `Websearch` when you assign `Optional<T?>.Null` (or plain
  `null`), which is how upstream deletes the key from the preferences document; omitting the member
  leaves the stored value alone.
- **The config family is on the client.** The `v2.0.2` snapshot added four operations and all four
  are generated and covered: `OpenCodeClient.Config` carries `GetPreferencesAsync`,
  `GetShellsAsync`, and `PatchUpdatePreferencesAsync` (global preferences read, the host shell
  catalog, and the preferences patch), and `SessionClient.PutPermissionRulesAsync` replaces a
  session's permission ruleset. `ConfigPreferences.Websearch` and
  `ConfigUpdatePreferencesPatchRequest.Websearch` are structural unions over `false` and a
  `ConfigWebSearchInfo` provider. Coverage is 138 of 143 pinned operations, up from 135 of 140 in 0.8.0-preview.1.

- **Sessions carry their permission ruleset, and changing it raises an event.**
  `SessionCreateRequest`, `SessionInfo`, and `SessionCreatedData` gained `Permissions`, and the new
  durable event `session.permissions.updated` materializes as `SessionPermissionsUpdated` on
  `IEvent`, `ISessionEventDurable`, and `ISessionLogItem`, carrying the session id and the new
  ruleset.

- **One door submits a command line on both terminal families.** `PtySession.SubmitAsync(string)`
  and `PersistentPtySession.SubmitAsync(string)` send the line plus the carriage return a
  terminal's Enter key sends, through the same serialized send path as the matching `WriteAsync` —
  a UTF-8 text message for a normal PTY, the framed input message carrying the viewport for a
  persistent one. The argument is exactly one line: a `\r` or an `\n` inside it is refused with
  `ArgumentException`, an empty line is a bare Enter, and nothing is trimmed. `WriteAsync` is
  unchanged and stays the raw door — partial input, control sequences, and bytes a terminal
  emulator produced.

- **A union interface now carries what all its members share.** A marked union whose members all
  declare a property in the same shape promises that property on its interface, so a generic
  consumer reads it without a `switch` over concrete types. The landmark case is the session log:
  `ISessionEventDurable` gained `Id`, `Created`, `Metadata`, `Location`, and `Durable`, and the 43
  per-event envelope records now implement one new `IDurableEnvelope` carrying `AggregateID`,
  `Seq`, and `Version` — so `item.Durable?.Seq` replaces a partial type switch. `IEvent`,
  `IFormField`, `IMcp`, `IReferenceSource`, `ISessionMessageInfo`, `ISessionMessageCompaction`,
  `ISessionInboxInfo`, `ISessionInboxItem`, `ISessionForkBoundary`, `IIntegrationAttemptStatus`,
  and `IIntegrationCommandAttemptStatus` gained members the same way, with `IMcpTimeout`,
  `ISessionMessageCompactionTime`, `IIntegrationAttemptStatusTime`, and
  `IIntegrationCommandAttemptStatusTime` as further carriers. Members are declared nullable because
  a union's `Unknown*` carrier preserves a raw payload and materializes none of them; the concrete
  records keep their existing non-nullable properties, the wire shape is unchanged, and no type was
  removed. Implementing one of these interfaces outside the SDK now requires the new members.

- **Session listing enumerates too.** `SessionsClient.EnumerateSessionsAsync` joins
  `SessionClient.EnumerateMessagesAsync` as an automatic cursor walk. A list query now binds the
  shared `ListRequest` spine whenever it *includes* `limit`, `order`, and `cursor` — extra filters
  no longer disqualify it — so `SessionListRequest` derives from `ListRequest` and gains the
  companion. Every filter of the first request rides each continuation unchanged; only the
  first-page-only `order` is dropped and the opaque cursor replaced.
- **Every automatic walk can be read as pages.** `Enumerate*Async` now returns
  `CursorSequence<TPage, TItem>`, whose `Pages` property yields each generated response envelope —
  `Status`, `Cursor`, `RawBody` and all — instead of only the items. There is no new page type and
  no page-size knob: a page is the response the server returned for one request. The item door and
  the page door are independent walks of the same recipe, so enumerating both sends both sets of
  requests.

### 🐛 Fixes

- **The docs no longer claim an anonymous opencode server exists, and a 401 now says so.** The
  guide and the shipped XML on `OpenCodeClientOptions.Password` told readers that `null` was the
  right value "for a server started without authentication". No such server can be started: the
  `opencode` CLI always runs its server with a password — the one set through `OPENCODE_PASSWORD`,
  or one it generates and prints as `server password <pw>` — and it rejects an uncredentialed
  request above the API layer, with an empty body and no typed error to read. Every statement of
  that claim is corrected, and when a call answers 401 while the client was built with `Password`
  left `null`, the `OpenCodeApiException` message now adds one sentence naming the missing
  credential and where to get one. The sentence appears for 401 only, and only when no password was
  configured; a rejected password keeps the plain message, and the `NoThrow` envelope is unchanged.
  No public member changed.
- **`OpenCodeServer.StartAsync()` now works on Windows with an npm-installed CLI.** npm writes shim
  files (`opencode`, `opencode.cmd`, `opencode.ps1`) and keeps the real binary inside
  `node_modules`, while `Process.Start` appends only `.exe` and ignores `PATHEXT` — so the shipped
  default `Command` failed on every npm-installed Windows machine with a bare "Failed to start the
  server command 'opencode'" and no stderr. The launcher now resolves `Command[0]` the way a shell
  does before spawning anything, so the `.cmd` shim is found and started. Nothing changes on Linux
  or macOS, where npm's bin entry is a link to the binary and the default already worked.

### 🔧 Changes

- **`Command[0]` is resolved before the process is created.** A path (rooted, or carrying a
  directory separator) is used as written; a bare name is searched through the `PATH` directories
  in order, skipping empty entries and resolving relative ones against the current directory. On
  Windows a bare name with no extension is tried with each `PATHEXT` extension in `PATHEXT` order
  (falling back to `.COM;.EXE;.BAT;.CMD`), and a name that already carries an extension is tried as
  written; on Unix the name itself is searched. A bare name that matches nothing now fails with an
  `OpenCodeServerException` that names the command, the number of directories searched, and the
  extensions tried, instead of the bare spawn error it used to surface.
- **A resolved Windows `.cmd`/`.bat` shim is launched through the system `cmd.exe`** (`/d /s /c`,
  every token quoted) rather than through `CreateProcess`'s implicit batch handling. Two consequences:
  `ProcessId` then reports the `cmd.exe` host rather than the server process — ask the server for
  `health.Health.Pid` when you need that one, and note that disposal's whole-tree kill still covers
  everything underneath — and a leading argument of yours containing `&`, `|`, `<`, `>`, `^`, `%`,
  `!`, `"`, CR, or LF is refused with `OpenCodeServerException` before anything starts, because
  `cmd.exe` re-parses the line (the fail-closed answer to BatBadBut / CVE-2024-24576). The SDK's own
  `--stdio --port 0` are unaffected, and non-batch targets are launched exactly as before.
- **The terminals guide no longer contradicts itself about Enter.** Its normal-PTY example wrote
  `"echo hello\r"` while its persistent-PTY example wrote `"echo hello\n"`. Both now run their
  command through `SubmitAsync`, and the terminator rule is stated once: Enter is `\r`, `\n` is a
  line feed that the Windows console host does not accept as a submit, and the SDK never rewrites
  what `WriteAsync` is given.

- **Snapshot additions.** `MessageListRequest.Type` filters a message list by message type
  (`MessageListRequestType`) and rides every continuation unchanged; `ModelInfo.Websocket` and
  `ProviderInfo.Websocket` are new optional flags; compaction ended and failed data, and the
  compaction message records, carry optional `Cost` and `Tokens`.
- `SessionClient.EnumerateMessagesAsync` returns `CursorSequence<MessageListResponse,
  ISessionMessageInfo>` instead of `IAsyncEnumerable<ISessionMessageInfo>`. `await foreach` over it
  is unchanged, and code that stored the result in a variable typed `IAsyncEnumerable<T>` still
  compiles, because `CursorSequence<TPage, TItem>` implements `IAsyncEnumerable<TItem>`. Code that
  declared the variable with `var` and then assigned an `IAsyncEnumerable<T>` to the same variable
  is the one shape that needs its type written out.
- `SessionListRequest` no longer declares `Limit`, `Order`, and `Cursor` itself; it inherits all
  three from `ListRequest`. Reading and initializing them is unchanged.

### 📚 Documentation

- **Durable replay now says which servers can actually do it.** The streaming guide and the
  README's known issues state that the distributed `opencode` CLI starts its server without event
  persistence and exposes no switch for it, so a replay from a CLI-started server answers with the
  `log.synced` marker alone — a contract-valid success with no history in it. Observed on
  `@opencode/cli@2.0.2`. A new live test on the ordinary pinned CLI profile asserts the
  marker-only answer and is the reversal trigger: when it fails, upstream began
  persisting by default and those statements change with it.
- **"Choosing a model" is a new section in the getting-started guide.** One compiled recipe —
  await plugin activation, read the provider and model catalogs, then place
  `new ModelRef { ProviderId = model.ProviderId, Id = model.Id }` on session creation — plus the
  three rules that surround it: health is process liveness and not catalog readiness, a session
  reference carries `ModelInfo.Id` and never `ModelInfo.ModelId`, and neither create nor prompt
  validates the reference. The same recipe runs in the in-repo sandbox's `--standalone` leg.
- **Session export documents what `Sanitize` does.** A sanitized export replaces message text with
  `[redacted:text:<id>]`-shaped placeholders while ids, types, order, and count survive, so it is
  for sharing a conversation's shape and never for comparing transcripts.
- **A refused worktree removal has a paragraph in the errors guide.** A 400 `WorktreeError` removed
  nothing — the directory and its inventory row both remain — and `ForceRequired` false means git
  ran and failed for a reason `Force` cannot fix, such as the Windows permission denial observed on
  the then-pinned `@opencode/cli@0.0.0-beta-19242` while another process held the directory.

## [0.8.0-preview.1] - 2026-09-09

The first published release. Everything below describes the surface as it ships; there is no
migration to perform, because no earlier version was ever published.

### ✨ New features

- **`OpenCodeAI.Sdk` — the typed client.** **135 of the 140 operations** in the pinned OpenAPI
  snapshot are callable across **28 client families**: sessions, PTYs, persistent PTYs, shells,
  events, MCP servers, integrations, projects, worktrees, workspaces, providers, language models,
  agents, skills, commands, forms, permissions, credentials, plugins, RPC, references, VCS,
  websearch, file system, generation, server, debug, and experimental. Every operation carries a
  generated request type and a generated response envelope; bound handles (`SessionClient`,
  `PtyClient`, `PersistentPtyClient`) partially apply a resource id over the shared pipeline.
- **Unions dispatch by tag, and tolerate what they do not know.** A discriminated union decodes by
  the literal tag the document declares. The live event stream additionally carries one
  prefix-tagged arm, so every `rpc.*` event dispatches to `EventRpc` — tried after the literal tags
  and before the carrier. Anything matching neither lands in that union's `Unknown*` variant with
  its tag and raw payload, so a server newer than the pinned snapshot widens an enumeration rather
  than breaking it.
- **A standalone server launcher.** `OpenCodeServer.StartAsync()` starts, monitors, and stops a
  private `opencode2 serve` child — generated lease credential, stdin-EOF ownership, bounded tree
  termination — and `CreateClient()` hands back a client already bound to it. An optional
  `OpenCodeServerOutput` collector, supplied through `OpenCodeServerOptions.Output`, retains a
  bounded tail of the child's stdout and stderr for pull snapshots that report truncation, and it
  stays readable after a failed start; `ProcessId` stays readable after disposal for the same
  diagnostic use. Real-process lifecycle acceptance runs on Windows, Linux, and macOS.
- **Server-sent event streaming.** `EventsClient.SubscribeAsync` follows the global bus and
  `SessionClient.GetLogAsync` follows one session's log, both as `IAsyncEnumerable<T>` of typed
  frames over the same transport, decoration, and status walls as one-shot calls. A body cut
  mid-event is reported rather than dispatched, and the contract's mid-stream failure channel
  surfaces as a typed exception instead of being discarded.
- **PTY and persistent-PTY terminal sessions.** `PtySession` and `PersistentPtySession` are
  hand-written WebSocket doors over a shared, family-neutral socket core: read frames, write input,
  dispose to close. The persistent family adds attach/handoff/snapshot semantics, byte-typed
  output and checkpoints, and the framed input protocol with viewport tracking.
- **Terminal connection lifetime a caller can drive.** A connection-owned receiver assembles
  frames into an internal queue independently of the caller's enumeration, so canceling a read ends
  only that wait: the same healthy connection can be read again, or written to, afterwards. Both
  connect options gained an init-only `SendTimeout` (default 30 seconds) bounding each input or
  resize send end to end, including the wait for the send gate. Connection termination delivers the
  frames it already holds before reporting its outcome, while disposal abandons unread frames and
  joins the connection's own work. A persistent PTY keeps its locally requested viewport coherent
  with the input it labels, rather than letting an inbound resize report overwrite it. See
  [the terminals guide](docs/guide/terminals.md#-cancellation-deadlines-and-disposal).
- **Dependency injection through `OpenCodeAI.Sdk.Extensions`.** `AddOpenCode(Action<…>)` or
  `AddOpenCode(IConfiguration)` registers one singleton client owning its transport for the
  container's lifetime, plus every sub-client resolved from that same instance — inject
  `SessionsClient` or `EventsClient` directly.
- **A typed error model.** Calls throw typed exceptions by default; `OpenCodeRequestOptions.NoThrow`
  returns the failure as data on the same envelope (`IsError`, `Error`) for the cases where a
  non-2xx is a normal answer. Transport failures map to their own exception family.
- **Cursor-based asynchronous pagination.** The two cursor-carrying list envelopes — session list
  and message list — expose the wire cursor directly, and `SessionClient.EnumerateMessagesAsync`
  follows it to exhaustion as an `IAsyncEnumerable<T>`.
- **Source-generated JSON.** `System.Text.Json` source generation throughout, with no reflection
  fallback anywhere in the serialization path. Both packages declare `IsAotCompatible` on
  `net10.0`; the one reflective seam, `AddOpenCode(IConfiguration)`, is annotated
  `[RequiresDynamicCode]` / `[RequiresUnreferencedCode]` so a trimmed or AOT build is warned rather
  than surprised.

### 🛠️ General

- **Target frameworks:** `netstandard2.0`, `net472`, `net8.0`, `net9.0`, `net10.0` — for both
  packages. The suite runs on `net472` on Windows, real-process launcher tests included, and on
  `net8.0`/`net9.0`/`net10.0` on all three OSes. `netstandard2.0` is a consumption target rather
  than a test target — it has no runtime to execute on, and the `net472` leg is what exercises its
  compile surface.
- **Protocol identity:** built against an accepted OpenAPI snapshot of upstream's `v2` branch, not
  a live branch. The exact commit, its digest, and the receipt-governed refresh procedure live in
  [`spec/SNAPSHOT.md`](spec/SNAPSHOT.md).
- **Generated output is committed and reviewed as source**, locked by a public-API baseline and
  verified by regeneration, so a protocol refresh arrives as a readable diff.
- **Test suite:** 5,138 tests green on Windows — the fullest leg, and the only one that adds the
  `net472` assemblies. Linux and macOS run the same suite on the three modern targets.

### 📋 Important Notes

- **Unofficial.** This project is not affiliated with or endorsed by the opencode team.
- **Three operations are declined by decision, not by omission.** `v2.config.get` and
  `v2.experimental.migration.v1.status` hit an undiscriminated object-union wall; `v2.fs.read` is
  a framework wildcard route with no OpenAPI path template to bind. Admitting any of them would
  mean inventing a contract upstream does not declare. See
  [API Coverage](README.md#-api-coverage).
- **Two operations are transport-owned, not missing.** `v2.pty.connect` and
  `v2.persistentPty.connect` are WebSocket upgrades, so they are served by the hand-written
  `PtySession` / `PersistentPtySession` doors rather than by generated code — fully usable, just
  not generated.
- **Pre-1.0 API.** The public surface is locked by a reviewed baseline, but it may still move
  before `1.0.0`. Breaking changes will be called out here with impact and migration path.

[0.9.0-preview.7]: https://github.com/opencode-dotnet/opencode-sdk-dotnet/releases/tag/v0.9.0-preview.7
[0.9.0-preview.6]: https://github.com/opencode-dotnet/opencode-sdk-dotnet/releases/tag/v0.9.0-preview.6
[0.9.0-preview.5]: https://github.com/opencode-dotnet/opencode-sdk-dotnet/releases/tag/v0.9.0-preview.5
[0.9.0-preview.4]: https://github.com/opencode-dotnet/opencode-sdk-dotnet/releases/tag/v0.9.0-preview.4
[0.9.0-preview.3]: https://github.com/opencode-dotnet/opencode-sdk-dotnet/releases/tag/v0.9.0-preview.3
[0.9.0-preview.2]: https://github.com/opencode-dotnet/opencode-sdk-dotnet/releases/tag/v0.9.0-preview.2
[0.9.0-preview.1]: https://github.com/opencode-dotnet/opencode-sdk-dotnet/releases/tag/v0.9.0-preview.1
[0.8.0-preview.2]: https://github.com/opencode-dotnet/opencode-sdk-dotnet/releases/tag/v0.8.0-preview.2
[0.8.0-preview.1]: https://github.com/opencode-dotnet/opencode-sdk-dotnet/releases/tag/v0.8.0-preview.1
