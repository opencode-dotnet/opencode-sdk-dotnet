# Testing Style — authoring tests

Date: 2026-09-26

Binding authorship style for every test in this repository. `quality-gates.md` owns the
current assurance posture and completion gates; operational build-out state belongs in
`../ROADMAP.md`. This document governs how tests are written inside that architecture and
applies `coding-style.md` to test code - test infrastructure is code and follows every rule
there.

## 1. Test infrastructure is a first-class citizen

Test setup is designed, reviewed, and refactored like product code — never accumulated
as copy-paste arrange blocks. The canonical shape is a scenario, context, and builder,
sized to need:

- **Inline scenarios are the default for small, one-off variations.** The shared scenario
  mechanism owns assembly (a fresh fake filesystem, rendering, and the context); the test
  supplies only a domain-aware builder action. A one-line variation does not earn a class
  and file:

  ```csharp
  var context = SpecScenario.Define(spec =>
      spec.WithSchema("ToolResult", schema => schema.Unrestricted()))
      .Build();
  var document = ingestion.Project(context.SpecPath);
  ```

- **Named scenario classes are promoted, not automatic.** A `sealed` scenario class earns
  one-concept/one-file status when at least one of these holds: the same arrangement is
  reused across test classes; setup is non-trivial (roughly more than five fluent
  statements, or an embedded fixture plus additional shaping); or the situation carries
  durable domain/landmark identity used across slices. The promoted shape:

  ```csharp
  internal sealed class ConfigPluginTupleScenario : SpecScenario
  {
      protected override void Arrange(SpecDocumentBuilder spec) =>
          spec.WithRawSchema("Config", "config-plugin-tuple.json");
  }

  var context = new ConfigPluginTupleScenario().Build();
  ```

  (Type names illustrative — the concrete infrastructure is named at slice planning.)
- **Domain-aware fluent builders.** Scenario state is expressed in domain verbs
  (`WithSchema(...)`, `WithOperation(...)`, `WithResponse(...)`), not raw file writes
  scattered through test bodies. Builders live in the owning test project's `Support/`
  area, compose over the canonical filesystem fake, and follow coding-style rules
  (sealed, no tuple returns, no concrete-collection parameters).
- **Grow on demand.** The scenario mechanism, context, and builder are the pattern; named
  scenarios, test hosts, and builder vocabulary grow only when a promotion/reuse signal
  fires. No speculative infrastructure, and no infrastructure duplication either: the
  second copy-paste of an arrange block first grows a reusable builder verb or preset;
  class promotion follows the rule above rather than file-count convention.

## 2. Filesystem rule

**Testably supplies the repository's only filesystem double** through the shared
`System.IO.Abstractions.IFileSystem` contract (sealed decision; the independent TestableIO
analyzer enforces it wherever an abstractions assembly is referenced — even test code reaches
`Path` through `IFileSystem.Path`):

- Levels 1–2 (unit, contract): `Testably.Abstractions.Testing.MockFileSystem`, assembled
  through the scenario builders.
- Level 3 and full-artifact smoke tests: `Testably.Abstractions.RealFileSystem`.
- Raw `System.IO` never appears in test code, and no second filesystem fake is ever
  introduced — one canonical fake per repository.
- Repository tooling and tests consume `IFileSystem` directly. Shipped SDK code carries no
  filesystem-abstraction dependency: the file access it performs sits behind its own internal
  seam (`IServiceFileSystem` for the background-service slice), whose test implementation is a
  `tests/Shared/` adapter over the same `IFileSystem`, so the fake stays the one above.

## 3. Test data policy — no inline dumps

**Raw JSON/XML/string-literal dumps pasted inline in test methods are forbidden.** A
test body states *intent*; the data it runs on lives in one of three sanctioned homes:

1. **Embedded fixture files** — under the owning test project's `Fixtures/` folder,
   loaded through a resource-loader helper by name. The default for wire-shaped data:
   one small file per quirk, named for the construct it isolates, reviewable on its own.
2. **Typed builders** — for variation families where files would multiply: red tests
   composing a valid base plus exactly one offending construct through the builder. The
   variation reads as a domain statement in the test body, not as a diff between two
   pasted strings.
3. **Centralized constants** — small, single-file-scoped values in a static
   `<Domain>Data` class. Never the same literal repeated across test methods.

A short literal is acceptable only when the literal *is* the subject under test (a
media-type string in a media-type parsing test). Data that describes structure always
goes through 1 or 2.

Representative wire fixtures prove runtime behavior, not exhaustive union membership. Do not
invent one hand-authored payload per branch and call that contract breadth: marked tag maps and
structural token-to-arm maps,
serializer registration, and plural membership are checked mechanically through the bound and
emitted plans, while a small schema-valid runtime corpus exercises framing and deserialization.
Real observed frames are promoted into that corpus when available. Documentation states the
observed fixture count rather than projecting structural completeness onto it.

Runtime tests assert transport/framing, JSON materialization, required .NET shape, and union
dispatch. They do not mutate otherwise representable payloads solely to prove that the SDK
revalidates an OpenAPI range, fixed literal, optional-null distinction, or collection child
constraint; those remain server responsibilities (ADR-0014). Generator tests still fail closed on
unsupported OpenAPI constructs and prove the exact required/null-representation C# mapping.

## 4. Fakes and mocks

- **Substitute at seams only** (NSubstitute over interfaces). Records, IR types, and
  pure classes are constructed, never mocked.
- **Never hand-build types you do not own** where a published contract can be exercised
  instead: ingestion fixtures load through the pinned reader — its DOM types are never
  constructed by hand in tests. This is the authorship side of the sealed
  no-mock-framework and fake-only-published-contracts principles.
- **No giant shared `TestBase`.** Shared behavior lives in scenario bases and small
  single-responsibility helpers; a base class accumulating unrelated conveniences is
  split like any other class (§1 red flags apply to test code).
- **If mocking hurts, fix the seam, not the test.** A painful mock setup means the
  production boundary is wrong — redesign the seam instead of layering test helpers
  over it.

## 5. TUnit mechanics

- Naming: `{Symbol}_Should_{Expected_Behavior}[_When_{Condition}]`. Symbol names stay intact as one
  token (`TryResolve`, `NuGet`); every other word is underscore-separated and starts with a capital.
  Test classes are `{Sut}Tests`, one class per file; promote a SUT to a folder with per-area test
  files only when it outgrows comfortable navigation.
- TUnit creates a fresh instance per test: setup belongs in the constructor or
  `[Before(Test)]`; no state carried between tests, no shared mutable fields.
- Assertions state intent in the test body — snapshot testing is reserved for its two
  sealed uses (emitter micro-snapshots and the public API surface lock); behavior tests
  never snapshot.
- Parallel keys and `[NotInParallel]` order tests inside one host only, while the hosts of one
  run — one per target framework — run at the same time. A resource two hosts must not use at
  once takes a named `MachineLock` (a file lock the operating system releases with a dead
  holder). A test whose load a runner cannot carry beside another host's timed tests carries a
  `TestCategories` category the documented gate runs in a pass of its own
  (`quality-gates.md`); background-service elections are the one such category.
- A test that compiles the whole SDK through `GeneratedSourceCompiler` carries
  `[ParallelLimiter<RoslynCompilationSlots>]`, so at most two such compilations run in a host at
  once. One compilation already uses every core; more at once only starve the hosts beside it.
  The compiler compiles each distinct set of emitted sources once per process, so tests that emit
  the same plan share one compilation.
- A test releases every thread, handle and process it starts, directly or through a server it
  launches. The launcher's exit watches carry a session-level guard: `LauncherReaderLeakGuard`
  fails the run when any POSIX exit-watch thread or Windows exit watch is still live at
  `[After(TestSession)]`, after the shared fixtures have been disposed.

## 6. Owned servers are hermetic

A live test runs against a server its fixture owns, and that server may read nothing the fixture
did not put there. No test, and no developer running `dotnet test`, exports anything to make that
true; the fixture hands the boundary to the child process it starts.

- **One environment policy.** `tests/Shared/ServerIsolation.cs` is the only place an owned launch
  takes its environment from: the XDG roots, the config root and database, an empty config seed, an
  emptied explicit config file, no model-catalog fetch, and a home that exists inside the fixture's
  own run root and gives an interactive shell nothing to ask (a terminal the server opens runs the
  user's own shell there, and zsh meets a home without startup files with a first-run wizard), with
  Windows' roaming and local application data roots (`APPDATA`, `LOCALAPPDATA`) inside it, where
  per-user stores such as gcloud's application default credentials would otherwise be read. A
  variable that can steer a server at the developer's data belongs there, set. The one thing
  fixtures share is bun's transpiler cache, kept beside the run roots: it is content-addressed
  output of the pinned source and carries no state, and leaving it under each isolated cache root
  made every source-run server start transpile the monorepo from cold. The map comes as an
  `IsolationBoundary` (`ServerIsolation.For`), which also carries the check that the server honoured
  it: every owner of a real server calls `ConfirmHonored` once the server is ready and before any
  test reaches it, and a server that opened no database under the run root stops the fixture there -
  it would otherwise be reading and writing the developer's own profile, which a later installed CLI
  that stopped reading one of the variables would do silently. Which server an owned fixture starts
  (`OPENCODE_SDK_TESTS_SERVER_COMMAND`, `OPENCODE_SDK_TESTS_ENDPOINT`) is a separate choice and not
  part of the boundary.
- **The host's own environment is scrubbed first.** A child inherits whatever the isolation map
  does not set, so `tests/Shared/InheritedEnvironment.cs` runs once per test session, before any
  fixture starts a child: it removes every `OPENCODE_*` variable except the suite's own
  `OPENCODE_SDK_TESTS_*` knobs, every `OTEL_*` variable, every provider credential and credential
  switch the pinned checkout names (`ProviderCredentialVariables`: each provider's `env` names in
  the bundled model catalog; every variable name the pinned provider and plugin sources spell or
  read, `type: "env"` methods and direct reads such as the Vertex project switches included, minus
  the terminal, shell and proxy names they also read; and the two the Google auth library reads,
  `GOOGLE_APPLICATION_CREDENTIALS` and `CLOUDSDK_CONFIG`), the whole `AWS_*` chain, and the git
  variables that address another repository (`GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`,
  `GIT_CEILING_DIRECTORIES`), and when a proxy variable is set it names loopback in `NO_PROXY`, so
  neither the test clients nor the bun server route `127.0.0.1` through the proxy. It prints the names it removed, never values. CI's environment
  carries none of these; the scrub exists for developer machines, where an exported variable
  otherwise looks like an SDK defect. Without the pinned checkout the credentials cannot be named,
  and the session still runs, because only a server reads them: `PinnedServerCommand`, through
  which every owned server is located, refuses instead. The hook then sets the `ServerIsolation`
  map, without `HOME`, `USERPROFILE`, `APPDATA` and `LOCALAPPDATA`, over a session root of its own
  (`SessionIsolation`), so a child that only inherits takes opencode's own roots from there, and the
  session fails at its end if a server opened a database there.
- **Run roots have a clean chain above them.** The pinned server loads project configuration from
  every directory between a location and the drive root, and resolves a workspace inside a
  repository to that repository's project, whatever the environment says - so where a run root
  lives is part of the boundary. `TestRunRootLocation` keeps run roots outside the user profile
  and outside every repository: under the machine-wide application data root on Windows, where the
  temp root sits inside the profile, and under the temp root elsewhere. It refuses, naming the path
  and `OPENCODE_SDK_TESTS_RUN_ROOT`, a directory that has a repository, project configuration, or
  a skills directory anywhere above it.
- **A client names a location.** The pinned server resolves a request without a directory to its
  own working directory, which bun needs anchored inside the upstream checkout. An owned fixture's
  `CreateClient()` therefore defaults to an empty directory of its own; a test passes a
  `LocationSelector` when the location is its subject. An external endpoint gets no default,
  because it may not share this machine's filesystem. Because a request without a location still
  loads configuration from above that working directory, `PinnedServerCommand.WorkingDirectory`
  refuses a checkout with `.opencode` or `opencode.json[c]` in its `external` directory or above
  it; the upstream checkout's own project configuration below that is pinned.
- **An owned process never outlives its fixture.** Teardown ends every process its test started and
  waits for each to be finished before it removes the run root: a server still running under a
  removed root recreates it and keeps running unowned. Finished means the process has closed its
  handles. On Windows the exit code is set before a killed process closes its files, and
  `HasExited`, `WaitForExitAsync`, and `GetProcessById` report it gone from that moment, so
  `HeldProcess` holds each process's handle from while it runs and waits for that handle to signal.
  The Ensure door releases the contenders it starts instead of returning them, the way the pinned
  loop does, so `EnsureServiceContext` runs every election through the internal
  `OpenCodeServer.EnsureWithSeamsAsync` with a `ContenderLedger` spawner, which records each
  contender as it is spawned; the isolated fixture process records into the same ledger. A contender
  is recorded as a `ProcessMark`, its pid with the start the operating system keeps for it — on
  Linux the tick count in `/proc/<pid>/stat`, because .NET's `Process.StartTime` there rests on a
  boot time each process derives from the wall clock, so two processes can read one start tens of
  seconds apart. Teardown keeps the order of the pinned CLI's own election test: the losers leave on
  their own before the winner is ended, because ending the winner first hands the registration to a
  loser that is still starting. A registration that still names a live process after everything
  recorded was ended fails the test.
- **A wrapper's deadline stays above the bound it wraps.** When a test helper wraps an operation
  that has its own named bound, the wrapper's deadline stays above that bound, so the named failure
  is the one reported. `OwnedCleanup` is the mechanism for cleanup: a step whose inner bound exceeds
  the shared budget is owned under its own (`Own(name, timeout, operation)`), derived from that
  bound rather than copied, and every step is timed, so a step that exceeds its budget names the
  steps before it and how long each took.
- **Fail fast, never skip.** A missing submodule, a missing install, or a contaminated run-root
  chain is an instructive error, not a skipped test.

## 7. Anti-patterns (never)

- Inline data dumps (§3), or the same literal appearing in two test methods.
- Real file I/O in a level-1/2 test; raw `System.IO` anywhere in tests.
- Full-spec count assertions against the pin — counts are research-doc facts, and a
  count test turns every legitimate spec refresh into noise.
- `Skip` outside a mechanism that is implemented, documented, and sanctioned by the current test
  architecture. The container conditional skip is the only such mechanism today. A platform-gated
  live leg is not a skip: it asserts the arm the platform can reach (the daemon-absent 503 where
  the opencode-pty daemon cannot run, the full flow where it can), names the branch it took, and
  both branches assert.
- A second filesystem fake, a resurrected retired helper, or infrastructure kept "just
  in case" — deleted code is recoverable from git.
- Testing library internals (the OpenAPI reader's conformance, TUnit itself, the BCL)
  — test our rules at our boundaries.
