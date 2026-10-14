# Background-service process control uses the platform's own primitives

Date: 2026-09-20

The stop door ends a process this SDK did not start: the registered background service, which the
pinned client's `terminate` signals by pid after comparing the registration's four fields. A pid
alone is reused once its process is gone, and `Process.Kill()` verifies nothing about a foreign pid
on Unix — it is `kill(pid, SIGKILL)` on the number — so the SDK identifies the process as (pid, start
time), the token Aspire's `DcpProcessIdentity` and psutil use, read through `Process.StartTime`
(`GetProcessTimes`, `/proc/<pid>/stat`, `proc_pidinfo`), and compares it immediately before every
signal and after every wait. On Unix the two rungs are `SIGTERM` and `SIGKILL` through a private
P/Invoke of `kill(2)` on `libc` — `LibraryImport` on the modern targets, `DllImport` on the
`netstandard2.0` asset where that generator is unavailable — because .NET 10's public `Process`
surface sends only `SIGKILL` (`Kill()`, `Kill(bool)`), `PosixSignalRegistration` only receives,
shelling out to `kill(1)` is excluded, and dropping the terminate rung diverges from upstream;
.NET's own `Kill` and the pinned client's runtime reach the same call one layer down. On Windows
another process cannot be signalled, so both rungs are `Process.Kill()` (`TerminateProcess`), which
is what libuv does with a `SIGTERM` there. The wait is the pinned client's own poll schedule
(`stopPollInterval` × `stopPollAttempts`, 50 ms and 100 at the pin), and every look reads the
process identity, so the process is gone when its identity is; the platform's own exit
notification is not used, because it reports a `TerminateProcess` before the pid has left the
process table and would need a platform-specific exception the poll does not. This is the first
platform interop in the shipped SDK (ADR-0027's contender spawn is the second), and ADR-0001's "no
process library" stands: it binds one function of the C library every process already has loaded. "libc"
is the portable spelling — `libSystem.Native`'s `SystemNative_LoadLibrary` maps that exact name to
the platform's own library (`LIBC_SO` from `<gnu/lib-names.h>` on glibc, `/usr/lib/libc.dylib` on
macOS, `libc.so.7` on FreeBSD) instead of probing for a file, and that loader serves CoreCLR's
managed resolution and native AOT alike, with the same mapping in CoreCLR's own PAL. The
`LibraryImport` generator needs the SDK project's `AllowUnsafeBlocks`; that is a compiler option
of this project, reaches no consumer, and the generated stubs and the native exit wait's one
reverse-P/Invoke function-pointer expression are the only unsafe code in the shipped SDK.

## Considered options

- Two hard kills, `Process.Kill()` on both rungs — rejected: no terminate rung, so a daemon that
  handles `SIGTERM` never gets to clean up (none does at the pin; the contract is the graceful
  half), and the ladder diverges from upstream's.
- `DllImport` on every target, to keep the project's unsafe-code switch off — rejected: two
  blittable integers marshal identically either way, but the switch is internal to this project
  and reaches no consumer, and the generated stub is the form .NET recommends and analyzes.
- A pidfd (`pidfd_open`, Linux 5.3) — rejected: not exposed by .NET, Linux-only, and the start-time
  token is still needed on the other two platforms.
- Shelling out to `kill(1)`, or a package for one symbol (`Mono.Posix`, `Tmds.LibC`) — rejected;
  the design's non-goals and ADR-0001's stance already exclude both.

## Consequences

- The identity token protects the window from the first look to the last signal. A pid reused
  *before* the stop runs is indistinguishable from a wedged daemon by the registration alone — the
  residual upstream carries too, since its `same()` compares registration fields only. Recorded,
  not solved; the registration file's write time could bound it if it ever matters.
- A zombie reads as gone: it has exited and serves nothing. Linux keeps its `/proc/<pid>/stat`
  readable with the start time unchanged, so the identity read also checks the state field for
  `Z`; macOS refuses a zombie's start time, which already reads as gone. ADR-0027 made this
  host the parent of the daemon it elects, and its contenders reap their own exits; the check
  covers a zombie another parent leaks. A process still there after the kill rung fails the stop
  and leaves the registration in place.
- The `netstandard2.0` asset's Unix arm compiles the `DllImport` form and runs on no CI leg;
  it is a recorded known gap, beside the file-mode gap of the same arm. A musl libc (Alpine) is
  outside the CI matrix as well: the loader falls back to `libc.so` there, unverified here.
- Reversal: .NET 11 adds `Process.Signal(PosixSignal)` and `SafeProcessHandle.Signal`, whose Unix
  body is the same `kill(2)`; the net11 light-up ADR-0001 plans replaces the P/Invoke on that
  target. Its `SafeProcessHandle.Open` and `TryGetProcessById` stay pid-keyed, so the identity
  token stays the SDK's own there as well.
