# The standalone launcher places and ends its server on platform primitives

Date: 2026-10-05

`OpenCodeServer.StartAsync` owns a private server (ADR-0031: upstream's `Standalone.start` is the
reference). Upstream decides two things about that child: where it lives in the process tree, and
how its owner ends it. On both points `System.Diagnostics.Process` gives a different result on every
target the packages ship today.

| Point | Upstream (Bun) | `Process` |
|---|---|---|
| POSIX placement | A new session (setsid). A Ctrl+C or SIGHUP from the owner's terminal never reaches the server, and the owner can signal the whole group the server leads. | Leaves the child in the owner's process group. A Ctrl+C reaches the server even when the host cancels it. |
| POSIX close | Rung 1 is SIGTERM to that group. If the root is still alive 3 s later, rung 2 is SIGKILL to it. | Can signal only one pid. |
| POSIX descendants | Descendants that detached into a session of their own (the persistent-terminal daemon, PTY shells) are left to the server. | `Kill(entireProcessTree: true)` walks parent pids and kills them too. |
| Windows | The child joins a process-wide job object (KILL_ON_JOB_CLOSE, BREAKAWAY_OK, SILENT_BREAKAWAY_OK, DIE_ON_UNHANDLED_EXCEPTION), so the server dies with its owner while its own children stay outside the job. Close is `taskkill /pid N /T /F`, falling back to `TerminateProcess(handle, 1)`. | Has no job. Hands the host's own inheritable standard handles to the child, so a parent capturing the host's output never sees end-of-stream while the server lives. |

The launcher therefore owns the child's creation and ending through internal seams over platform
primitives, one seam per primitive:

- **POSIX spawn.** `posix_spawnp` with `POSIX_SPAWN_SETSID`, every signal reset to its default and an
  empty mask (libuv's child state, which `Process` does not reset for an ignored signal), and the
  standard streams wired by file actions.
  - On macOS it adds `POSIX_SPAWN_CLOEXEC_DEFAULT`, as libuv does. The platform has no atomic
    close-on-exec pipe, so this is what keeps one server's pipe ends out of another child.
  - Pipe creation and spawn share one SDK-wide lock.
  - A working directory uses `posix_spawn_file_actions_addchdir_np`. Where the C library predates it
    (glibc before 2.29), the child is started through `/usr/bin/env -C <dir>`, which keeps the pid
    across `exec` and leaves the signal state alone.
  - POSIX systems other than Linux and macOS are refused.
  - Every binding has a fixed, non-variadic C signature, because a variadic function's arguments are
    passed differently on Apple arm64.
  - This generalizes the contender spawn (ADR-0027), and the contender uses the same spawn.
- **Group signal.** `kill(-pgid, signal)` through the `kill(2)` binding of ADR-0026.
- **Windows job.** One lazily created, never-closed job per process with libuv's four limits. Each
  server is assigned to it right after creation; an assignment refused with ACCESS_DENIED is ignored,
  as libuv does. The host itself is not added: the server cannot observe that, and it would change
  the host's own crash behaviour.
- **Windows spawn.** `CreateProcessW` with an explicit inherited-handle list, so the server receives
  only its own standard handles.
- **Exit status.** On POSIX, a background thread per child blocks in `waitid` without reaping, then
  reaps the child under the lock the pid fallback takes and decodes the wait status into an exit code
  or a terminating signal. Under the same lock, the pid fallback first asks `waitpid` without
  hanging whether the pid is still an unreaped child, and signals only one that is. It polls only
  when `SIGCHLD` is ignored (see Consequences), and installs no process-wide signal handler. On
  Windows the launcher reads the exit code from the process handle.

Each seam has an implementation per target framework, `LibraryImport` on the modern targets and
`DllImport` on the downlevel assets.

## The ladder

The ladder follows upstream's rungs. Where the two end in a different place, the difference is
listed under the recorded divergences.

- **POSIX.**
  1. Stop collecting output.
  2. If the root has already exited, decide by its status:
     - a non-zero code and no signal: end the group's survivors the same way;
     - an unknown status: probe the group with `kill(-pgid, 0)` and end any survivors the same way;
     - otherwise stop here.
     Signalling the group cannot reach an unrelated process while the group still has members,
     because its id cannot be reused until then.
  3. SIGTERM to the group, falling back to the pid. If both fail, end with no escalation.
  4. Within `GracefulShutdownTimeout`, wait for the root's exit, then for the group to empty
     (`kill(-pgid, 0)` until ESRCH). Upstream instead waits for the root's own output pipes to close.
     The two differ only at the edges recorded below.
  5. SIGKILL to the group if members remain.
  6. Close stdin and release the handles.
  A failed start runs the same ladder with the configured grace, as upstream's scope close does.
- **Windows.**
  1. `taskkill /pid N /T /F` at once. If it exits non-zero, `TerminateProcess(handle, 1)`.
  2. If the root is still alive after `GracefulShutdownTimeout`, the same pair again.
  3. Release the handles.

## Recorded divergences (ADR-0031)

| Divergence | Upstream | Launcher | Reason |
|---|---|---|---|
| Taskkill path | Resolves `taskkill` through `cmd.exe` and `PATH` | Runs the absolute System32 path | A writable `PATH` entry cannot substitute the executable. |
| Already-exited root on Windows | Can run `taskkill` on the dead pid while it waits for the output deadline | Does not | That pid can already belong to an unrelated process. |
| Exit report | Reports an exit up to a second late when a descendant holds stdout | Reports the root's exit as soon as it is observed | Earlier and exact. |
| Forced-exit wait | Waits on the root's exit alone after SIGKILL | Bounds it at 10 seconds | Disposal never hangs on a process the kernel cannot end. |
| Windows exit code | Bun truncates it to 8 bits | Reports the full 32-bit code | The value the operating system gives. |
| Stderr | Discards it | Drains it into a bounded tail for startup diagnostics, and also honours upstream's `OPENCODE_PRINT_LOGS=1` inherit mode | A superset of upstream's behaviour. |
| Signals to the owner | Closes the scope on SIGINT and SIGTERM | Installs no process-wide signal handler | A library does not take over its host's signal handling; the host ends the server through `DisposeAsync`. |
| Waiting for the group | Waits for the root's own output pipes to close | Waits for the root's exit, then probes the group until it is empty | No dependency on who holds the pipes. There are three consequences, all recorded here. A same-group member that ignores SIGTERM but holds none of the root's pipes is ended by SIGKILL; upstream leaves it running. A detached descendant that holds the root's stdout does not delay the close; upstream waits the full grace and then SIGKILLs the group. A root that exits 0 while its stdout is still open leaves the group untouched; upstream SIGTERMs it within its 1 s output deadline. |

## Target frameworks

No target the packages ship today has these primitives in the BCL or in a faithful Polyfill
backport, so the seams are the SDK's own code on all of them. When `net11.0` joins, each seam is
checked against its .NET 11 primitive, primitive by primitive. Where the primitive gives upstream's
result, the `net11.0` build uses it. Where it does not, the own seam stays and the reason is recorded
here.

What .NET 11 gives, per primitive:

- `ProcessStartInfo.InheritedHandles` gives the explicit inherited-handle list.
- `WaitForExitAsync` returning `ProcessExitStatus` gives the exit status.
- Windows pipes are opened overlapped.
- `StartDetached` starts the new session, but keeps the child's ignored signals ignored.
- `Process.Signal` signals a single pid only.
- The `KillOnParentExit` job lacks SILENT_BREAKAWAY_OK, so the server's children would die with the
  owner.

## Considered options

- **Keep `Process` and accept its behaviour.** Rejected. A host that cancels Ctrl+C still loses its
  server. A normal close orphans the server's children. An owner crash on Windows leaves a server
  that ignores end-of-stream running indefinitely. Detached descendants that upstream deliberately
  leaves alone get killed. Every one of these is observable and none has a reason (ADR-0031).
- **Use .NET 11's `Process` features only.** Rejected for today. The shipped targets do not have
  them, `net10.0` is supported until November 2028, and three of the six primitives are not
  equivalent even on .NET 11. They are adopted per primitive once `net11.0` is a target.
- **Close stdin first and let the server shut down gracefully.** Rejected as the first rung. A server
  that exits on end-of-stream releases none of its children: the end-of-stream lease reaches only
  the root, so the children are orphaned. On Windows upstream ends its server by force, so the
  server already tolerates being ended that way.

## Consequences

- `client-runtime.md` § Launcher describes the placement, the ladder, and the divergences above as the
  status quo.
- The exit status the seams decode is the source of the launcher's public exit outcome.
- The launcher's acceptance stays real-process on Windows, Linux, and macOS. Every test releases
  every process it starts.
- In some hosts something else reaps the server first:
  - On a host running as PID 1, the runtime reaps every child once a SIGCHLD handler exists.
  - On Linux with SIGCHLD ignored, the kernel reaps.
  - On macOS with SIGCHLD ignored in-process, the exit is not reported to a waiting parent at all.

  The launcher detects an ignored SIGCHLD at start and falls back to a probe at most 100 ms apart.
  In each of these cases it reports the exit with an unknown status rather than inventing one, and
  the ladder still probes the group and ends its survivors. On macOS an ignore the host inherited
  across `exec` leaves the child a zombie; the probe reaps it and reports its real status.
- Reversal: upstream changes `Standalone`'s placement or ladder, or a .NET primitive becomes
  equivalent on every supported target.
