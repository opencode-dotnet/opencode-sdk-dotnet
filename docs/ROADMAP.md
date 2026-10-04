# Roadmap

Date: 2026-10-04

Operational state: what ships today, what is queued next, what is still open, and what is known to
be incomplete. This file is a summary and shrinks as work lands. `../AGENTS.md` routes to the
current architecture and engineering canon; decision records live in `adr/`. The live operational
queue is on the [project board](https://github.com/orgs/opencode-dotnet/projects/1).

## Status

**Pre-release, and the protocol surface is complete.** The callable surface is generated from an
accepted OpenAPI snapshot and rides one hand-written transport runtime.

- **Protocol pin** — generation reads an accepted snapshot of upstream's OpenAPI document taken at
  a release tag, never a live branch, and refreshes are receipt-governed (ADR-0020).
  `../spec/SNAPSHOT.md` owns the exact commit and the refresh procedure.
- **Coverage** — **138 of 140 operations selected** across 27 client families, none declined, and
  2 transport-owned that hand-written WebSocket doors cover, so all 140 are usable; `src/OpenCode.Sdk/.generation-incomplete` is
  the committed marker and names every one. One-shot calls, server-sent event streams (the global
  bus and the per-session log), PTY and persistent-PTY WebSocket sessions, cursor pagination, typed
  errors with `NoThrow`, and the standalone launcher (`OpenCodeServer.StartAsync`) are landed.
- **Assurance** — the suite runs on `net8.0`, `net9.0`, and `net10.0` across Linux, Windows, and
  macOS, plus `net472` on Windows, the fullest leg; `engineering/quality-gates.md` owns the gate a
  change must pass before it is called done.
- **Terminal lifetime corrections (D)** — consumer cancellation, send deadlines, local viewport
  ordering, and shared cleanup are implemented and verified on Windows across the four runnable
  targets. Linux and macOS live verification also passed on net8/net9/net10, including the persistent daemon
  round trip and normal PTY reuse after read cancellation. `architecture/client-runtime.md` and
  ADR-0023 own the contract.
- **2.0.22 refresh** — the accepted pin follows upstream's release tags, now `v2.0.22`, with no
  compatibility layer between them. 2.0.22 adds two operations, both generated: listing and
  creating stored credentials (`Credentials.ListCredentialsAsync`, `CreateCredentialAsync`), whose
  key and OAuth tokens are masked when printed. `session.create` gains `ParentId` and a declared 404,
  and cancelling a form can carry a `Message`. The service config's new `disabled` key is read only by
  CLI commands (its connection-mode selector and pairing guard), never by the background-service
  doors; the strict reader
  validates it as upstream decodes it. Upstream's committed OpenAPI document is stale against its
  own generator at this tag (four operations missing, a query parameter under the wrong
  operation; [anomalyco/opencode#53105](https://github.com/anomalyco/opencode/issues/53105)), so
  the snapshot is always produced by upstream's pinned generator, never copied (ADR-0020). Every
  watched input under the hand-written doors was reviewed: the PTY changes are type renames, and
  the launcher's inputs are unchanged apart from the standalone endpoint's new `exited`. Each
  refresh's CI run qualifies every leg at its pin: Windows on `net472`, `net8.0`, `net9.0`, and
  `net10.0`, Linux and macOS on `net8.0`, `net9.0`, and `net10.0`, with regeneration and receipt
  verification passing.
- **Upstream 2.x** — the upstream team launched the 2.x line publicly on 2026-09-26. It ships as
  the `@opencode/cli` npm package, the one this SDK pins and its guides install; the 1.x line
  continues beside it as `opencode-ai` (`1.18.33` on 2026-09-28), and upstream's GitHub Releases page
  still lists 1.x releases. The pin tracks upstream 2.x release tags and is refreshed under receipt
  before a release. The accepted protocol identity is owned by `../spec/SNAPSHOT.md`.
- **Packages** — the two packages publish as `OpenCodeDotNet.Sdk` and
  `OpenCodeDotNet.Sdk.Extensions` (the assemblies stay `OpenCode.Sdk`) and pack at the
  single-sourced `VersionPrefix 0.9.0`. Every `master` push publishes a `0.9.0-nightly.*` build to
  the organization's GitHub Packages feed, and `0.9.0-preview.6` is on NuGet.org, owned by
  `OpenCode.NET` and pushed through the manual lane over Trusted Publishing. The ids carry
  `OpenCodeDotNet` because nuget.org reserves the `OpenCode.` prefix for an unrelated owner. The
  earlier ids, `OpenCodeAI.Sdk` and `OpenCodeAI.Sdk.Extensions`, receive no further versions.

## Road to 1.0

`1.0.0` is the first stable release; the public surface freezes before it. Releases stay
`0.9.0-preview.N` until the freeze, then ship as `1.0.0-rc.N`, then `1.0.0`; between those points a
pin refresh or a fix round ships as the next preview or release candidate. Each workstream has a
short plan in the private companion repository when it starts, and the project board holds the
live queue. In order:

1. **Support policy.** The MCP server's repository (ADR-0030), the target-framework policy
   (ADR-0002), and the end of the .NET 5–7 promise
   ([#51](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/51)). **Complete.**
2. **Target-framework transition.** `net11.0` joins now on its go-live release candidate and
   `net8.0` and `net9.0` leave now, ahead of their end of support on 2026-11-10, across packages,
   tests, CI legs, the public API check, and the docs; no non-preview package is built on a
   pre-GA SDK. A consumer whose target falls back to the `netstandard2.0` asset on an unsupported
   runtime gets a build warning. It follows an upstream refresh and `0.9.0-preview.6`, and the
   launcher's .NET 11 pipe fix (Known Gaps) lands first. Launcher parity work rides the same
   workstream: the disposal ladder first matches upstream's scope close (a SIGTERM to the process
   group before the forced kill, and the Windows kill's exit code), and after the pipe fix and
   `net11.0`, `OpenCodeServer.Exited` reports how a standalone server ended — exit code or signal,
   as upstream's `exited` does — on every target and OS. Exit: every leg runs `netstandard2.0`,
   `net472`, `net10.0`, and `net11.0` as its platform allows.
3. **Maintainability review.** A time-boxed, read-only review of the code, the generator, the
   tests, and the canon at current `master`. Each finding goes to one of four places: the API
   review, the 1.0 performance gate, a small fix now, or a parallel track. Exit: one triaged
   finding list.
4. **API review.** First the namespace layout — family namespaces or today's flat
   `OpenCode.Sdk` and `OpenCode.Sdk.Models` — decided from a measured model-ownership analysis and
   a one-family pilot. Then the parked handle questions (a parent-mediated handle door for flat
   single-action families, a handle client's resource id as a property), the Extensions API
   baseline and the remaining package, test-roster, and Native AOT assurance (#51), exclusion
   fingerprints for the transport-owned operations (ADR-0008), and a bound for the terminal
   receive queue, which is unbounded today. Exit: every surface question is decided.
5. **Extension points.** How retry, telemetry, and hooks attach (the policy roster, ADR-0018),
   and whether validated client configuration splits from the transport factory, decided so that
   each capability lands additively after `1.0.0`. The public network-timeout option and the
   per-operation event-stream idle bound it gates ship here, closing the half-open stream gap.
   Exit: no planned capability needs a breaking construction change. The surface then freezes and
   `1.0.0-rc.1` ships.
6. **1.0 performance gate.** The benchmark coverage reviewed and extended with representative
   scenarios, a generator baseline, and a full run of the suite on the release candidate. Exit: a
   reviewed full run with no unexplained regression.
7. **`1.0.0`.** Exit: `1.0.0` on NuGet.org, the `OpenCodeAI.*` popularity moved to
   `OpenCodeDotNet.*`, and the README's rename notice gone.

## Parallel tracks

These do not block `1.0.0` and run beside the road.

- **MCP server** in its own repository (ADR-0030), a thin adapter over the published SDK. Design
  and scaffolding can start now; code against the SDK waits for the namespace decision.
- **Maintainability follow-ups** that change no public surface: the folder layout (after the
  namespace decision), the ADR and canon review, dead code, test-support ownership, and the
  source-watch review.
- **References point one way:** about 170 comments in source, tools, tests, and workflows still
  cite documentation (`docs/…` paths and ADR numbers), against `engineering/documentation.md`.
  Each explains its status quo locally instead; generator-emitted comments change through the
  generator.
- **Test categorization**, so a lane can run a named subset. No test carries a category today; the
  only split is by project and by the `*LiveTests` / `*ContractTests` names.
- **Operations:** automation for the upstream observation lanes (tip detector, candidate refresh),
  a quarantine lane, the nightly source-run canary with the performance suite (ADR-0022),
  Restore-patch retirement, the operation inventory and assurance ledger (ADR-0022: a contract
  test for every status arm the pinned document declares, verifier-checked, with the unreachable
  arms listed by name), larger CI runners now that the repository is in an organization (paid),
  and a publish-lane diet (the nightly job's `generate --verify` repeats what the same run's lint
  job already proved).

## After 1.0

- Retry, telemetry, and hooks themselves, added through the extension points `1.0.0` fixes.
- A Hosting package, if a concrete consumer — the MCP server or a first external host — needs
  host-owned process lifecycle.

## Open Questions

- **`session.log` resume guarantees** — the pinned document exposes `after` as an optional
  string, and the generated surface stays faithful to it; ADR-0013 forbids importing the narrower
  type upstream's implementation decodes. Replay mechanics and retention are established and
  carried by canon. What stays open is the wire behaviour nothing upstream pins: no server-level
  test covers this route, so the status a malformed `after` answers, and whether an idle `follow`
  connection survives an intermediary, are settled only by this repository's own live tests.
  Upstream has no production caller that passes `after`, so this SDK is the path's first consumer.
- **OpenAPI projection fidelity** — the pinned document loses detail upstream's implementation
  carries. Confirmed losses are reported upstream
  ([anomalyco/opencode#44911](https://github.com/anomalyco/opencode/issues/44911), restored by the
  proposed [PR #45182](https://github.com/anomalyco/opencode/pull/45182)); further candidates are
  parked for filing at the maintainer's choosing — off-convention `persistentPty.*` operation ids,
  a missing security-scheme declaration, 25 lost `Config.Info` descriptions, an undeclared header
  value, a numeric range and a file path both invisible behind bare strings, a WebSocket close code
  overloaded across two causes, and two declared arms the handler cannot produce. Findings stay
  diagnostic and never feed generation or curation (ADR-0013).
- **Release notes** — each release's notes are its CHANGELOG section; whether that flow needs more
  than a copy into the GitHub Release is open.
- **The generator's remaining binding-locality extractions** — parked until a generator change
  needs them.

## Known Gaps

- **On the .NET 11 runtime the launcher's Windows output readers cannot be released early.**
  .NET 11's `Process` opens a child's stdout and stderr read ends overlapped on Windows
  (dotnet/runtime#125643). The launcher reads them synchronously on dedicated threads and ends a
  read that a surviving descendant keeps open with `CancelSynchronousIo`, which cannot cancel a
  read on an overlapped handle; disposal then returns after its bound with the reader thread still
  waiting for end-of-stream. This follows from the runtime's source and is not yet observed; it
  affects any target the SDK was built for once it runs on .NET 11. The fix chooses the reader by
  the pipe's observed mode and reads asynchronous pipes through one cancellable reader, which also
  ends the Unix drain's wait on a pool thread while a descendant holds the pipe.
- **An opencode server on Windows can die inside its native file watcher.** `@parcel/watcher` 2.5.1
  crashes the server process when a directory it watches natively is written to while
  subscriptions to it are being released and re-created, which the server does per location for
  the skills directories of every `.claude`, `.agents`, and `.opencode` root it discovers between a
  location and the drive root. A caller sees `OpenCodeTransportException`, not an SDK fault. The
  test fixtures are hermetic against it (`engineering/testing-style.md`); a consumer's server is
  not. The defect is
  [parcel-bundler/watcher#262](https://github.com/parcel-bundler/watcher/issues/262), where the
  standalone reproducer from this repository's investigation is on record.
- **The downlevel Unix arm of the background-service door is compiled, not run.** Discovery's
  one-time copy of an older hashed registration and Ensure's persistent-terminal handoff sidecar,
  which carries a terminal-adoption ticket, are created exclusively at mode `0600`: `net8.0` and
  later set the mode at creation through `FileStreamOptions.UnixCreateMode`, while `net472` and
  `netstandard2.0` have no such API and apply it through Polyfill's `File.SetUnixFileMode`, which
  spawns `chmod` with an unquoted path and no exit-code check, so a failed `chmod` leaves the file
  at the process umask. The stop door's Unix signal rungs and the contender spawn are `DllImport`s
  on that asset, where the `LibraryImport` generator the modern targets use is unavailable
  (ADR-0026, ADR-0027). The registration copy is a convenience the daemon's own registration
  supersedes and the ticket carries its own expiry; the population of that arm is Mono on Unix
  (`net472` is Windows-only), and no CI leg runs the combination, so it is recorded rather than
  tested; the README's Known Issues carries the consumer-facing sentence. Reopens if a supported
  target ever needs that arm or if Polyfill quotes the path.
- **A pid reused before `StopAsync` runs is indistinguishable from a wedged daemon.** The
  registration names a pid and no process start time, so a daemon that died and whose pid the
  operating system handed to an unrelated process before a stop ran looks, to the file, like the
  registered service still there. The identity token (ADR-0026) closes the window from the stop's
  first look to its last signal — the upstream client compares registration fields only — and the
  residual before it is upstream's too. Recorded, not solved; the registration file's write time
  could bound it if it ever matters.
- **Two allocation follow-ups are queued behind a benchmark gate** — on `net472` and
  `netstandard2.0` a response body over 1 MB costs one wire-sized copy, and each terminal connection
  allocates one 16 KiB receive buffer, reused across consumer reads. Both are described for consumers
  in the README's Known Issues; pooling requires evidence under the current connection-lifetime harness.
- **Durable session-log replay cannot be enabled on any distributed CLI build.** `events.persist`
  is a server-library option: the `opencode` serve command declares no flag for it, bridges no
  environment variable to it, and reads no configuration key for it, so a replay from a CLI-started
  server is one `log.synced` marker whose sequence advanced with no durable events before it
  (confirmed at the pin; observed on `@opencode/cli@2.0.2`). The SDK is faithful to the route — the
  gap is upstream capability — and consumers are told in the README's Known Issues and the
  streaming guide. Reversal trigger:
  `tests/OpenCode.Sdk.Tests/Sessions/SessionLogCliProfileLiveTests.cs`; when it fails, upstream
  began persisting by default and the guide, the README, and the canon sentence in
  `docs/architecture/client-runtime.md`'s server-sent-events section all change together.
- **A half-open event stream is not detected.** A successful SSE body stays live until caller
  cancellation, server completion, or failure, so a connection whose peer is gone without closing
  hangs a consumer that supplied no cancellation of its own; ordinary resets, server exits, and
  killed processes already surface at once. A consumer cannot bound this from outside the SDK,
  because the server's keepalive comments carry no event and never reach the enumeration. The bound
  has to be per operation rather than a property of every SSE body: upstream writes a keepalive
  every fifteen seconds on `event.subscribe` and none on `session.log`, whose follow mode is
  silent by design while a session is idle. The network-timeout option in the extension-points
  workstream closes it, so the bound arrives configurable rather than as a behavior no caller can
  widen. Upstream's own clients
  place this one layer above their core client, which does not reconnect either
  (`packages/client/src/solid/connection.ts`: two-second connect, forty-five-second idle abort,
  one-second reconnect delay, and an authoritative refetch once reconnected).
- **Live tests are serialized by one mutex within a host** ([#83](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/83)):
  every live class that shares a server carries the `ServerProcess` key, and the classes whose
  assertions ride a wall-clock bound run keyless `[NotInParallel]`, so a host's live tests run one
  at a time. The part of that serial tail a key could shorten measured under 30 s of a Windows
  run, whose critical path is the build, so it is not queued. Bounded parallelism
  (`ParallelLimiter`) would also need the simulated drive controller demultiplexed by session.
- **Small cleanups queued for their next natural touch** — `envelopePayloadNames` is the one
  curation section whose rows cannot carry a reason (a mechanical loader change, though authoring
  fifteen verified reasons is not); the generator still inlines the dot-segment refusal into every
  route builder instead of calling the shared policy (a large but purely mechanical generated
  diff); the `form` group's curation reason is written in the future tense where every sibling
  states present fact; the `MedianNanoseconds` benchmark column breaks the other columns'
  abbreviation convention; the committed sandbox's `--paginate` mode exits nonzero on an empty
  enumeration; and the public API baseline renders `typeof(X)` attribute arguments as `typeof(X?)`
  on interfaces whose members are mostly nullable — a PublicApiGenerator artifact of the
  compiler's nullable-context compression, harmless and stable, to be normalized in the baseline
  test and reported upstream.
- **Three one-off test failures were seen once each and never reproduced.** No runner named a test
  and re-runs of the same binaries were green, so this is a measurement gap rather than a known
  defect: run the gates with `--report-trx --report-trx-filename <unique>` so a recurrence names it.
- **CodeQL analyses C# without a build.** Code scanning runs on default setup, which extracts C#
  in `build-mode: none`. That leaves the database under CodeQL's own confidence thresholds — 83% of
  calls resolve to a target against a threshold of 85%, and 89% of expressions carry a known type
  against the same 85% — which its guidance attributes partly to generated source, and this
  repository commits a large generated surface. No alert is open, so the gap is possible false
  negatives rather than a known defect. Closing it means retiring default setup for an advanced
  `codeql.yml` that builds: full type resolution, bought with a workflow whose failure would stop
  scanning silently rather than loudly.
- **`BuildOs`/`BuildArch` in `Directory.Build.props`** need their values adapted to opencode's
  release-asset naming when the binary-download need lands. `BuildOs == 'windows'` already adds the
  net472 test targets, and opencode's assets use the same `windows` name.
- **The launcher's descendant-termination proof has a platform boundary.** The startup-tree tests
  prove the direct child exits immediately and the grandchild terminates inside a ten-second bound
  on the modern target frameworks (all three OSes) and on `net472` Windows (`taskkill /T`). The
  downlevel non-Windows arm of the tree kill (a plain `Kill()`) is not exercised by any test project,
  and the Linux/macOS behavior is established only by the three-OS CI run, never by a Windows-local
  suite. On Unix the observed grandchild is not a child of the test process, so its exit is visible
  only once the adopting parent reaps it: an environment without a reaping PID 1 fails that bound
  with the process still recorded as a zombie.
