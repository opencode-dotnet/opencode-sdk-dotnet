# Detached contender spawn uses the platform's own process-creation primitives

Date: 2026-09-21

The Ensure door starts a service contender this SDK does not keep: a detached child that must
outlive the caller, inherit no stdin or stdout, and keep a stderr pipe only for election
diagnostics. Upstream's `spawnServiceContender` asks Node for `detached: true` and
`stdio: ["ignore", "ignore", "pipe"]`. On Unix that is libuv's `UV_PROCESS_DETACHED` —
`posix_spawnattr` `POSIX_SPAWN_SETSID` (glibc 2.26+, Darwin 19+), else a fork child `setsid()`
then exec — which opens a new session with no controlling terminal and is not in the parent's
process group; parent-exit survival does not require it there, SIGINT/SIGHUP isolation does. On
Windows it is `DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP` with NUL for the ignored handles,
because a non-detached Node child is assigned to a `JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE` job and
because `DETACHED_PROCESS` cannot combine with `CREATE_NEW_CONSOLE`. `unref` is event-loop
refcounting, not an OS flag. `System.Diagnostics.Process` cannot express that contract on any
current target: `RedirectStandard*` false inherits the console, true creates pipes that EPIPE
when the parent exits, and ignore is `/dev/null`/`NUL`, which survives; .NET 10's
`CreateNewProcessGroup` is Windows-only `CREATE_NEW_PROCESS_GROUP` (Ctrl+C disabled for the new
group) with no `DETACHED_PROCESS`, no `setsid`, and no `Standard*Handle`; its Unix PAL is
fork/vfork plus execve. The internal `IServiceContenderSpawner` seam therefore binds the
platform's own process-creation primitives — `CreateProcessW` on Windows with
`DETACHED_PROCESS | CREATE_NEW_PROCESS_GROUP`, a Unicode environment block, NUL stdin/stdout, a
stderr pipe, and an explicit inherited-handle list; `posix_spawnp` on Unix with
`POSIX_SPAWN_SETSID`, `/dev/null` on 0 and 1, and a stderr pipe on 2 — `LibraryImport` on the
modern targets, `DllImport` on the `netstandard2.0` asset where that generator is unavailable.
The executable is the one the shipped launcher resolution produced, launched through `cmd.exe`
on the launcher's own `BatchCommandLine` line when it is a batch shim. The seam refuses when the platform cannot provide the session-detach
flag; it never falls back to `Process.Start`, a shell, or a managed `fork()`. This is the spawn
counterpart of ADR-0026's `kill(2)`, and ADR-0001's "no process library" stands: it binds
functions of the C library and the Windows process API every process already has loaded. ADR-0032
extends the same POSIX spawn to the standalone launcher. Two spawn-failure
classes exist at the pin (upstream's client `ensure`): a synchronous throw from
`spawnServiceContender` fails Ensure immediately (`Effect.try`), while the child's asynchronous
`error` event is kept as the first startup failure and reported once no contender is left
alive. Native `CreateProcessW`/`posix_spawnp` report ENOENT synchronously, so the
.NET port maps a synchronous spawn failure to the immediate class and preserves the platform
error as the inner exception.

## Considered options

- `Process.Start`, including .NET 10's `CreateNewProcessGroup` — rejected: no session detach, no
  NUL/`/dev/null` handles, and the Unix PAL still has no `setsid`; parent-exit survival and
  SIGHUP isolation would diverge from upstream's.
- Reusing the standalone `CreateProcess` (stdin lease, stdout readiness) — rejected: a
  different contract; a contender is released, not owned, and must survive the caller.
- CliWrap, or shelling out to `setsid(1)` / `nohup` / `start` — rejected; the design's
  non-goals and ADR-0001's stance already exclude both.
- `DllImport` on every target — rejected for the same reason as ADR-0026: the generated stub
  is the form .NET recommends, and the unsafe-code switch is already on for `kill(2)`.
- A managed `fork()` plus `setsid` fallback when `POSIX_SPAWN_SETSID` is missing — rejected:
  the seam is fail-closed on the session-detach flag; libuv's ENOSYS fork path is not imported.

## Consequences

- Reversal: .NET 11 adds `Process.StartDetached` and `StandardInputHandle` /
  `StandardOutputHandle` / `StandardErrorHandle`. The light-up must set both `StartDetached`
  and `CreateNewProcessGroup` on Windows (`StartDetached` alone is only `DETACHED_PROCESS`;
  verified against dotnet/runtime `release/11.0` @ `d5666ccb`), and on Linux the runtime
  detaches with fork + `setsid` in the child rather than `POSIX_SPAWN_SETSID` (the flag
  exists only on its macOS arm) — the same observable result as this seam's `posix_spawnp`
  path. `KillOnParentExit` stays off, because the service is shared.
  .NET 10's `CreateNewProcessGroup` is not that trigger: NUL and `DETACHED_PROCESS` are still
  missing on Windows, and Unix still has no `setsid`.
- The `netstandard2.0` asset's Unix arm compiles the `DllImport` form of `posix_spawnp` and
  runs on no CI leg — Mono on Unix, the same Known Gap as Stop's `kill(2)` and the file-mode
  arm.
- A synchronous spawn failure is a public `OpenCodeServerException` with the platform error
  inside; an asynchronous child `error` after a successful create is election-loop business,
  not this seam's.
- On Unix the stderr pipe is the BCL's `AnonymousPipeServerStream`, created by the runtime's own
  native code: both ends are close-on-exec and only the write end crosses, as fd 2 through
  `posix_spawn_file_actions_adddup2`, and its reads wait on the runtime's event loop. The C
  library's `pipe` and `fcntl` are not bound: `fcntl` is variadic, and a fixed-signature
  P/Invoke reads its third argument from the wrong place on Apple arm64.
- On Windows the stderr pipe is a local named pipe made the way .NET 11's `Process` makes its
  output pipes (dotnet/runtime#125643) and libuv its child stdio: a fresh
  `\\.\pipe\LOCAL\` name, a single instance that refuses a pre-existing name and remote clients,
  an overlapped read end, and a synchronous inheritable write end that enters the handle list
  beside NUL. An anonymous pipe is synchronous on Windows, so each pending read would hold a
  pool thread for as long as the contender keeps stderr open — for the elected service, its
  whole life — and ten concurrent Ensure callers were measured to stall the host's pool for
  about 13 seconds. The overlapped read waits on the completion port. On both platforms the
  parent drops its copy of the write end after the spawn.
- The Unix child starts with every standard signal at its default disposition and an empty mask
  (`POSIX_SPAWN_SETSIGDEF | POSIX_SPAWN_SETSIGMASK` over `sigfillset`/`sigemptyset` sets), the
  state libuv gives a Node child; `posix_spawn` alone keeps ignored dispositions (the .NET host
  ignores `SIGPIPE`) and the calling thread's mask. glibc's internal `SIGCANCEL`/`SIGSETXID`
  stay the C library's own.
- The executable is looked up through the launching process's `PATH`, by the launcher
  resolution, not through a `PATH` the overlay carries; Node searches `options.env.PATH`.
- The SDK reaps its own contenders: libuv reaps on `SIGCHLD`, which a library cannot install
  without taking the host's handler, so each contender polls its own exit from the spawn on
  (`waitpid(WNOHANG)` on Unix, `GetExitCodeProcess` on Windows) on a 1 ms to 1 s backoff with no
  parked thread, and on Unix keeps polling after disposal until the pid is reaped. A contender is
  finished, in Node's sense of `close`, once its stderr reached its end and its exit was observed.
