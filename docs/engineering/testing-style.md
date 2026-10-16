# Testing Style — authoring tests

Date: 2026-10-17

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
  var document = await new SpecIngestion(context.FileSystem)
      .IngestAsync(context.SpecPath, CancellationToken.None);
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

  (`SpecScenario` and `SpecDocumentBuilder` live in `tests/OpenCode.Sdk.Tools.Tests/Support/`; the
  promoted class name is illustrative.)
- **Domain-aware fluent builders.** Scenario state is expressed in domain verbs
  (`WithSchema(...)`, `WithOperation(...)`, `WithResponse(...)`), not raw file writes
  scattered through test bodies. Builders compose over the canonical filesystem fake and
  follow coding-style rules (sealed, no tuple returns, no concrete-collection parameters);
  where they live follows the test layout below.
- **Grow on demand.** The scenario mechanism, context, and builder are the pattern; named
  scenarios, test hosts, and builder vocabulary grow only when a promotion/reuse signal
  fires. No speculative infrastructure, and no infrastructure duplication either: the
  second copy-paste of an arrange block first grows a reusable builder verb or preset;
  class promotion follows the rule above rather than file-count convention.

### Test layout

- A test mirrors its subject's source path with every `Internal` segment dropped:
  `Sessions/Internal/X.cs` is tested in `Sessions/XTests.cs`, `Internal/Transport/Pipeline.cs`
  in `Transport/PipelineTests.cs`. Test namespaces follow test folders, so a test folder named
  like a BCL type shadows it there: tests under `Debug/` declare `OpenCode.Sdk.Tests.Debug` and
  qualify `System.Diagnostics.Debug`. A guard test refuses two source types that project onto
  one test path.
- In SDK.Tests, a helper or fixture used by one slice lives in that slice's `Support/` or
  `Fixtures/`. Helpers and fixtures used by several slices stay in the test project's root
  `Support/` and `Fixtures/`. Embedded fixtures retain their `Fixtures.` resource-name prefix
  through explicit `LogicalName` metadata. Live-server harness code lives in
  `tests/OpenCode.Sdk.Tests/Harness/`. Files another test project links, and the
  embedded-fixture loader, stay in `tests/Shared/`, compiled through explicit links. There is no
  separate test-support project.
- The mirror rule also applies to test infrastructure: `Harness/X.cs` is tested in
  `Harness/XTests.cs`, and `Support/X.cs` is tested in `Support/XTests.cs`. A subject with
  several test files keeps those files beside the subject.

## 2. Filesystem rule

**Testably supplies the repository's only filesystem double** through the shared
`System.IO.Abstractions.IFileSystem` contract (sealed decision; the independent TestableIO
analyzer enforces it wherever an abstractions assembly is referenced — even test code reaches
`Path` through `IFileSystem.Path`):

- Unit and contract tests: `Testably.Abstractions.Testing.MockFileSystem`, assembled
  through the scenario builders.
- Tests that run a real process or server, and full-artifact smoke tests:
  `Testably.Abstractions.RealFileSystem`.
- Raw `System.IO` never appears in test code, and no second filesystem fake is ever
  introduced — one canonical fake per repository. Fault injection decorates that fake: an
  NSubstitute `IFileSystem` that delegates to the Testably double and fails the chosen calls is
  the same fake with a failure, not a second one.
- Repository tooling and tests consume `IFileSystem` directly. Shipped SDK code carries no
  filesystem-abstraction dependency: the file access it performs sits behind its own internal
  seam (`IServiceFileSystem` for the background-service slice), whose test implementation is a
  `tests/OpenCode.Sdk.Tests/Harness/TestablyServiceFileSystem.cs` adapter over the same
  `IFileSystem`, so the fake stays the one above.

## 3. Test data policy — no inline dumps

**Raw JSON/XML/string-literal dumps pasted inline in test methods are forbidden.** A
test body states *intent*; the data it runs on lives in one of three sanctioned homes:

1. **Embedded fixture files** — in the `Fixtures/` folder of the slice that uses them, or the test
   project's root `Fixtures/` when several slices share them, loaded through a resource-loader helper
   by name. The default for wire-shaped data:
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
  constructed by hand in tests. This is the authorship side of the fakes-only-for-published-contracts
  posture (`quality-gates.md`).
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
- Assertions state intent in the test body — snapshot testing is reserved for its three
  sealed uses (emitter micro-snapshots, the public API surface lock, and the suppression
  ledger); behavior tests never snapshot.
- Parallel keys and `[NotInParallel]` order tests inside one host only, while the hosts of one
  run — one per target framework — run at the same time. A resource two hosts must not use at
  once takes a named `MachineLock` (a file lock the operating system releases with a dead
  holder). A test whose load a runner cannot carry beside another host's timed tests carries a
  `TestCategories` category the documented gate runs in a pass of its own
  (`quality-gates.md`): background-service elections and the packed native AOT consumer.
- A test that compiles the whole SDK through `GeneratedSourceCompiler` carries
  `[ParallelLimiter<RoslynCompilationSlots>]`, so at most two such compilations run in a host at
  once. One compilation already uses every core; more at once only starve the hosts beside it.
  The compiler compiles each distinct set of emitted sources once per process, so tests that emit
  the same plan share one compilation.
- A test releases every thread, handle and process it starts, directly or through a server it
  launches. The launcher's exit watches carry a session-level guard: `LauncherReaderLeakGuard`
  fails the run when any POSIX exit-watch thread or Windows exit watch is still live at
  `[After(TestSession)]`, after the shared fixtures have been disposed.
- A proof that a launcher path leaves no handle behind counts only the kinds that path creates, and
  compares them exactly: pipe descriptors on Linux and macOS (`HostDescriptors`), and on Windows
  the handles on other processes and their threads, pipe ends and jobs (`LauncherHandleCensus`).
  A whole-process count is never the measure: it moves with the runtime's own thread churn, which no
  launcher path controls. A wait before the count is for an asynchronous release the path starts
  and names, and for nothing else.

## 6. Owned servers are hermetic

A live test runs against a server its fixture owns, and that server may read nothing the fixture
did not put there. No test, and no developer running `dotnet test`, exports anything to make that
true; the fixture hands the boundary to the child process it starts.

- **One environment policy.** `tests/OpenCode.Sdk.Tests/Harness/ServerIsolation.cs` is the only
  place an owned launch
  takes its environment from: the XDG roots, the config root and database, a temp directory
  (`TMPDIR`, and `TMP` and `TEMP` on Windows), an empty config seed, an emptied explicit config
  file, no model-catalog fetch, project configuration discovery off, and a home that exists inside
  the fixture's own run root and gives an interactive shell nothing to ask (a terminal the server
  opens runs the user's own shell there, and zsh meets a home without startup files with a
  first-run wizard), with Windows' roaming and local application data roots (`APPDATA`,
  `LOCALAPPDATA`) inside it, where per-user stores such as gcloud's application default
  credentials would otherwise be read. A variable that can steer a server at the developer's data
  belongs there, set. The temp directory moves the server's own temp root, which agents may use as
  an approved external directory and which would otherwise be shared with the developer's
  opencode. Discovery off is upstream's own switch, `OPENCODE_DISABLE_PROJECT_CONFIG=1`, which the
  pinned server and the simulation host both read: discovery otherwise walks from every location to
  the drive root and loads each ancestor's `opencode.json[c]`, `.opencode` directory, `.claude` and
  `.agents` skills and `AGENTS.md`, together with the git references that configuration declares;
  for a request without a location that walk starts inside the upstream checkout, whose own
  configuration makes the server clone a GitHub repository. With it off, the server reads only the
  isolated global config root and home. The one test whose subject is project configuration (the
  workspace skill) starts a server of its own through `ServerIsolation.ForProjectConfiguration`,
  the same map with the switch set to `0` - set, because every child of the session inherits it
  on - and puts its workspace inside that server's run root, as upstream's own config tests keep
  discovery on and put the project in a temp directory. The one thing fixtures share is bun's
  transpiler cache, kept beside the run roots: it is content-addressed output of the pinned source
  and carries no state, and leaving it under each isolated cache root made every source-run server
  start transpile the monorepo from cold. The map comes as an `IsolationBoundary`
  (`ServerIsolation.For`), which also carries the check that the server honoured it: every owner
  of a real server calls `ConfirmHonored` once the server is ready and before any test reaches it,
  and a server that opened no database under the run root stops the fixture there - it would
  otherwise be reading and writing the developer's own profile, which a later installed CLI that
  stopped reading one of the variables would do silently. Which server an owned fixture starts
  (`OPENCODE_SDK_TESTS_SERVER_COMMAND`, `OPENCODE_SDK_TESTS_ENDPOINT`) is a separate choice and not
  part of the boundary.
- **Machine-wide configuration is moved or refused.** The pinned server also reads a system
  configuration directory and, on macOS, MDM managed-preference plists, and loads them last, as
  managed settings that override every source the map sets, its empty seed included. On Windows
  the directory follows `ProgramData`, so the map gives each server an empty `ProgramData` inside
  its run root. On Linux (`/etc/opencode`) and macOS (`/Library/Application Support/opencode` and
  the `ai.opencode.managed.plist` files under `/Library/Managed Preferences`) no variable moves
  them, so `ServerIsolation.For` refuses an owned launch while one exists
  (`tests/OpenCode.Sdk.Tests/Harness/SystemConfigurationSources.cs`).
- **The host's own environment is scrubbed first.** A child inherits whatever the isolation map
  does not set, so `tests/OpenCode.Sdk.Tests/Harness/InheritedEnvironment.cs` runs once per test
  session, before any
  fixture starts a child: it removes every `OPENCODE_*` variable except the suite's own
  `OPENCODE_SDK_TESTS_*` knobs, every `OTEL_*` variable, every provider credential and credential
  switch the pinned checkout names (`ProviderCredentialVariables`: each provider's `env` names in
  the bundled model catalog; every variable name the pinned provider and plugin sources spell or
  read, `type: "env"` methods and direct reads such as the Vertex project switches included, minus
  the terminal, shell and proxy names they also read; and the three an auth tool reads whether or
  not the source spells them: `GOOGLE_APPLICATION_CREDENTIALS` and `CLOUDSDK_CONFIG` for the Google
  auth library, `AZURE_CONFIG_DIR` for the Azure CLI), the whole `AWS_*` chain, and the git
  variables that address another repository (`GIT_DIR`, `GIT_WORK_TREE`, `GIT_INDEX_FILE`,
  `GIT_CEILING_DIRECTORIES`), and when a proxy variable is set it names loopback in `NO_PROXY`, so
  neither the test clients nor the bun server route `127.0.0.1` through the proxy. It prints the
  names it removed, never values. CI's environment carries none of these; the scrub exists for
  developer machines, where an exported variable otherwise looks like an SDK defect.
  `PinnedServerCommand`, through which every owned server is located, refuses to start a server
  until the scrub has run, and refuses instead of the session when the pinned checkout is missing
  and the credentials cannot be named: only a server reads them. The hook then sets the
  `ServerIsolation` map, without `HOME`, `USERPROFILE`, `APPDATA`, `LOCALAPPDATA` and the temp
  variables (the test host takes its own temp directory, and on Linux and macOS its run roots, from
  them), over a session root of its own (`SessionIsolation`), so a child that only inherits takes
  opencode's own roots from there and loads no project configuration, and the session fails at its
  end if a server opened a database there.
- **Run roots have a clean chain above them.** The server whose subject is project configuration,
  and an installed command an override names, which may not read the switch, load it from every
  directory between a location and the drive root, and every server resolves a workspace inside a
  repository to that repository's project, whatever the environment says - so where a run root
  lives is part of the boundary. `TestRunRootLocation` keeps run roots outside the
  user profile and outside every repository: under the machine-wide application data root on
  Windows, where the temp root sits inside the profile, and under the temp root elsewhere. It
  refuses, naming the path and `OPENCODE_SDK_TESTS_RUN_ROOT`, a directory that has a repository, or
  any project configuration discovery loads, skills directories included, anywhere above it.
- **A client names a location.** The pinned server resolves a request without a directory to its
  own working directory, which bun needs anchored inside the upstream checkout. An owned fixture's
  `CreateClient()` therefore defaults to an empty directory of its own; a test passes a
  `LocationSelector` when the location is its subject. An external endpoint gets no default,
  because it may not share this machine's filesystem. The upstream checkout's own project
  configuration is not harmless - it carries skills, plugins, tools and a git reference the server
  clones - and owned servers keep it out with discovery off, which
  `ProjectConfigurationIsolationLiveTests` proves at that working directory. What would still walk
  from there is the server whose subject is project configuration, for a request without a
  location, and an installed command an override names, which may not read the switch. For them
  `PinnedServerCommand.WorkingDirectory` refuses a checkout with configuration that would run
  inside the server - `.opencode` or `opencode.json[c]` - in its `external` directory or above it.
  One `.opencode` is accepted: the checkout's own, at its root, holding exactly the edit-time
  Slopwatch plugin with its pinned SHA-256 and no link. Any other entry, place or content is
  refused, and the plugin registers nothing in a server whose location is not the repository root.
  Skills directories there are not refused: a checkout under a home that keeps its own skills is
  the usual Linux and macOS layout, and such a server would read them but run nothing from them.
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
- **The event decides a verdict, never the clock.** A test whose verdict rests on a product bound
  passing, or not passing, runs that bound on `ManualTimeProvider`, which moves only when the test
  moves it, and moves it only after the event the verdict rests on. The pipeline's progress window
  takes its clock through the internal `Pipeline` constructor and the launcher's early-end drain
  through `LauncherSeams.Time`. A test that needs the bound running first waits for the timer to
  be created. Every other upper bound in a test is a hang guard: it decides no verdict, and only a
  hang reaches it.
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
- Real file I/O in a unit or contract test; raw `System.IO` anywhere in tests.
- Full-spec count assertions against the pin — a count describes one snapshot, and a
  count test turns every legitimate spec refresh into noise.
- `Skip`. This document sanctions no skip mechanism; a missing prerequisite is an instructive
  failure (§6). A platform-gated live leg is not a skip: it asserts the arm the platform can reach
  (the daemon-absent 503 where the opencode-pty daemon cannot run, the full flow where it can),
  names the branch it took, and both branches assert.
- Retrying, widening a bound for, or quarantining a flaky test. A flaky test is fixed at its root
  cause; a test is deleted only with evidence that its subject is gone or that another test proves
  the same thing.
- A second filesystem fake, a resurrected retired helper, or infrastructure kept "just
  in case" — deleted code is recoverable from git.
- Testing library internals (the OpenAPI reader's conformance, TUnit itself, the BCL)
  — test our rules at our boundaries.

## 8. Mutation proofs

A change that adds or fixes a critical rule carries one mutation proof per rule. Invert or remove
the rule in product code, show that a named test fails for the asserted reason, then restore it. A
proof counts only when:

1. the slice is committed, or the mutated file holds no other uncommitted edit; the restore is by a
   saved copy or `git checkout HEAD --`, and the diff is byte-identical afterwards;
2. the mutated tree builds, and the record says whether it builds under the full analyzer build or
   only with `RunAnalyzers=false`. A mutant the full build rejects for a semantic diagnostic (dead
   code, a tautology, an unused member, a nullable error) is recorded as unreachable through that
   rule ID, not as a kill. A mutant rejected only for its spelling is re-spelled in an analyzer-clean
   form;
3. every binary the test exercises was rebuilt, including the service fixture's own SDK copy;
4. it fails on every target framework the rule ships on;
5. a test run that prints no total, or a total of 0, is not a result.

Mutation-tool scores are advisory. A tool that compiles mutants without the analyzer wall reports
kills that the real build would reject, so its results are replayed through the full build before
anyone relies on them.
