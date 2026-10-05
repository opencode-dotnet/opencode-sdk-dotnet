# The standalone launcher places and ends its server on platform primitives

Date: 2026-10-05

`OpenCodeServer.StartAsync` owns a private server (ADR-0031: upstream's `Standalone.start` is the
reference). Upstream decides two things about that child: where it lives in the process tree, and
how its owner ends it. On both points `System.Diagnostics.Process` gives a different result on every
target the packages ship today.

| Point | Upstream (Bun) | `Process` |
|---|---|---|
| POSIX placement | A new session (setsid). A Ctrl+C or SIGHUP from the owner's terminal never reaches the server, and the owner can signal the whole group the server leads. | Leaves the child in the owner's process group. A Ctrl+C reaches the server even when the host cancels it. |
| POSIX close | Rung 1 is SIGTERM to that group. After a grace period, rung 2 is SIGKILL to the group. | Can signal only one pid. |
| POSIX descendants | Descendants that detached into a session of their own (the persistent-terminal daemon, PTY shells) are left to the server. | `Kill(entireProcessTree: true)` walks parent pids and kills them too. |
| Windows | The child joins a process-wide job object that ends it when its owner exits. The server's detached descendants stay outside the job; its other children end with it. Close is a forced tree kill (`taskkill /T /F`). | Has no job. Hands the host's own inheritable standard handles to the child, so a parent capturing the host's output never sees end-of-stream while the server lives. |

The launcher therefore owns the child's creation and ending through internal seams over platform
primitives, one seam per primitive:

- **POSIX spawn.** `posix_spawnp` with `POSIX_SPAWN_SETSID`, every signal reset to its default and an
  empty mask (libuv's child state, which `Process` does not reset for an ignored signal). This
  generalizes the contender spawn (ADR-0027), and the contender uses the same spawn.
- **Group signal.** `kill(-pgid, signal)` through the `kill(2)` binding of ADR-0026.
- **Windows job.** One process-wide job: the server dies with its owner, and the descendants it
  detaches stay outside the job. The host itself is not added: the server cannot observe that, and it would
  change the host's own crash behaviour.
- **Windows spawn.** `CreateProcessW` with an explicit inherited-handle list, so the server receives
  only its own standard handles.
- **Exit status.** The launcher observes and reaps its own child without installing a process-wide
  signal handler, and never signals a pid that is no longer its own unreaped child.

Each seam binds its functions the way ADR-0026 binds `kill(2)`.

## The ladder

The ladder follows upstream's rungs. On POSIX: SIGTERM to the server's process group, then up to
`GracefulShutdownTimeout`, then SIGKILL to any member left. On Windows: the forced tree kill at
once, not stdin end-of-stream first. A failed start runs the same ladder. Where the launcher ends in
a different place from upstream, the difference is a recorded divergence.

## Recorded divergences (ADR-0031)

| Divergence | Upstream | Launcher | Reason |
|---|---|---|---|
| Already-exited root on Windows | Can run `taskkill` on the dead pid while it waits for the output deadline | Does not | `taskkill` cannot reach a process that has exited, so the run only costs time. |
| Exit report | Reports an exit late when a descendant holds stdout | Reports the root's exit as soon as it is observed | Earlier and exact. |
| Stderr | Discards it | Drains it into a bounded tail for startup diagnostics, and also honours upstream's `OPENCODE_PRINT_LOGS=1` inherit mode | A superset of upstream's behaviour. |
| Signals to the owner | Closes the scope on SIGINT and SIGTERM | Installs no process-wide signal handler | A library does not take over its host's signal handling; the host ends the server through `DisposeAsync`. |
| Waiting for the group | Waits for the root's own output pipes to close | Waits for the root's exit, then for the group to empty | No dependency on who holds the pipes. |
| Forced-exit wait | Waits on the root's exit alone after the forced kill | Bounds that wait | Disposal never hangs on a process the kernel cannot end. |
| Windows exit code | Bun truncates it | Reports the code the operating system gives, in full | The exact value. |

## Target frameworks

No target the packages ship today has these primitives in the BCL or in a faithful Polyfill
backport, so the seams are the SDK's own code on all of them. When `net11.0` joins, each seam is
checked against its .NET 11 primitive, primitive by primitive. Where the primitive gives upstream's
result, the `net11.0` build uses it. Where it does not, the own seam stays and the reason is recorded
here.

On .NET 11 the inherited-handle list and the exit status are equivalent. These are not:

- `StartDetached` starts the new session, but keeps the child's ignored signals ignored.
- `Process.Signal` signals a single pid only.
- The `KillOnParentExit` job lacks the silent-breakaway limit libuv sets, so the descendants the
  server detaches would die with the owner.

## Considered options

- **Keep `Process` and accept its behaviour.** Rejected. A host that cancels Ctrl+C still loses its
  server. A normal close orphans the server's children. An owner crash on Windows leaves a server
  that ignores end-of-stream running indefinitely. Detached descendants that upstream deliberately
  leaves alone get killed. Every one of these is observable and none has a reason (ADR-0031).
- **Use .NET 11's `Process` features only.** Rejected for today. The shipped targets do not have
  them, `net10.0` is supported until November 2028, and three of the five primitives are not
  equivalent even on .NET 11. They are adopted per primitive once `net11.0` is a target.
- **Close stdin first and let the server shut down gracefully.** Rejected as the first rung. A server
  that exits on end-of-stream releases none of its children: the end-of-stream lease reaches only
  the root, so the children are orphaned. On Windows upstream ends its server by force, so the
  server already tolerates being ended that way.

## Consequences

- `client-runtime.md` § Launcher is the status-quo home for the placement, the ladder, and the
  divergences.
- The exit status the seams decode is the source of the launcher's public exit outcome.
- The launcher's acceptance stays real-process on Windows, Linux, and macOS.
- Where something other than the launcher reaps the server first (a runtime running as PID 1, an
  ignored SIGCHLD), the exit is reported with an unknown status rather than invented, and the ladder
  still ends the group's survivors.
- Reversal: upstream changes `Standalone`'s placement or ladder, or a .NET primitive becomes
  equivalent on every supported target.
