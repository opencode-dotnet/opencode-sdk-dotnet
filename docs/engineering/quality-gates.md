# Engineering Quality Gates

Date: 2026-10-17

Canonical quality policy for product code, generated output, repository tooling, tests, analyzers,
performance evidence, and completion claims.

## Assurance posture

The repository is defensive and fail-closed by default. Guard public preconditions and local
representability, assert internal invariants, and fail loudly rather than guess. Do not turn that
posture into a second validator for server-owned OpenAPI constraints; the runtime boundary is
canonical in `../architecture/protocol-and-generation.md` and ADR-0014.

Assurance intensity follows blast radius:

1. Shipped SDK runtime receives the strongest transport, materialization, multi-TFM, and behavioral
   evidence.
2. Committed generated output is protected by reviewed diffs, the analyzer wall, regeneration,
   public-API locks, and contract tests.
3. Repository tooling internals receive targeted evidence for their actual consumers and known
   lossy seams. Every additional mechanism must name a consumer or a concrete failure it prevents.

The testing posture is deliberately strict: observation-based gates, deterministic tests, fakes
only for published contracts, TUnit on Microsoft.Testing.Platform, real-process integration where
the process boundary is the product, and three-OS launcher acceptance. A flaky test is fixed at its
root cause. It is never retried, given a wider bound, or quarantined.

## Analyzer policy

The analyzer wall is fail-closed and final. Build owns compiler, SDK CA, third-party analyzer,
source-generator, and build-enforceable IDE diagnostics; the warning-level style formatter owns
Roslyn rules classified `EnforceOnBuild.Never` plus deterministic import organization. Do not
relitigate or broadly weaken either side to make a change pass. Redundant rules are deliberate where
their coverage differs or supplies defense in depth. When a rule misfires on valid code, use a
narrowly scoped per-rule arbitration comment that names the winning rule or contract; never roll
back the policy globally. `.editorconfig` contains the established pattern. A suppression of any
kind follows the suppression policy in `slop-review.md`.

- A rule that cannot fire here but still costs build time is switched off compilation-wide in the
  repository-root `.globalconfig`, with the same winner-naming reason and a removal trigger. A
  file-scoped `.editorconfig` severity hides the diagnostic, but the analyzer still runs: Roslyn
  skips an analyzer only when all its diagnostics are off for the whole compilation.
- `LangVersion=14.0` and `AnalysisLevel=10.0` are deliberate numeric pins. Never replace either
  with `latest`.
- C# 14 on net472 is unsupported by the platform documentation but deliberate repository practice,
  enabled by source polyfills. Future language changes move the numeric pin intentionally.
- `GenerateDocumentationFile=true` is load-bearing because CLI builds do not report IDE0005
  consistently without it. Keep the adjacent guard comment in `Directory.Build.props`.
- Analyzer package currency is manual. Since SDK 9, the build does not warn reliably when the
  pinned NetAnalyzers package falls behind the SDK; deliberate analyzer-package updates own that
  comparison explicitly.
- Repository-wide analyzer packages inherit into every project, and global warning escalation has
  no `WarningsNotAsErrors` escape. The few project `NoWarn` rows apply equally to build and format,
  so build mechanically owns enabled warning/error diagnostics from every third-party analyzer.
- Product code uses `ConfigureAwait(false)`, triple-enforced by CA2007, MA0004 in `Always` mode,
  and VSTHRD111. Tests are exempt from all three rules.
- `CA1502` attributes local functions declared in top-level statements to the compiler-generated
  `Main`, so a top-level `Program.cs` accumulates every mode's dispatch complexity onto one
  method. Split the dispatch into a named class instead of arbitrating the rule;
  `tests/OpenCode.Sdk.Sandbox/SandboxRunner.cs` records the precedent.

Internal research, 2026-08-08, "Analyzer & .editorconfig policy: claim verification + community
survey", records the claim verification, community comparison, and rule arbitration behind
this policy.

## Completion gate

"Builds" and "works" are different claims. A change is complete when the CI its pull request
triggers is green: the lint job and the three operating-system legs run every step of the full gate
listed below. Which changes skip those jobs, and what a merge requires, are in `workflow.md`.

Before a pull request opens, run the local check from the repository root. It catches failures
before they cost a CI run. A current handoff may add scope-specific checks but cannot weaken it.

1. The static steps: slopwatch, the Release build and both format passes, exactly as the full gate
   below runs them, and the clone gate (`verify-clones`, below).
2. The whole `OpenCode.Sdk.Tools.Tests` project except its `NativeAot` category; its repository
   guards and registries read every file, so a narrow filter misses them.
3. The test project of every product area the change touches, on every target framework the host
   builds, except the `ServiceElection` and `NativeAot` categories: `OpenCode.Sdk.Tests` for
   `src/OpenCode.Sdk/`, `tests/OpenCode.Sdk.Tests/` and `tests/Shared/`; `OpenCode.Sdk.Extensions.Tests`
   for `src/OpenCode.Sdk.Extensions/`; both, and `OpenCode.Sdk.Tools.Tests` again, for a change to
   `Directory.Build.*`, `Directory.Packages.props`, `global.json` or `dotnet-tools.json`.
4. The `ServiceElection` pass when the change touches the background service or its election, and
   the `NativeAot` pass when it touches packaging, trimming or publishing.
5. The generator checks below, for a protocol, generator, curation or generated-output change.

```bash
dotnet test --project "$PWD/tests/OpenCode.Sdk.Tools.Tests/OpenCode.Sdk.Tools.Tests.csproj" --configuration Release --no-build -- --treenode-filter "/*/*/*/*[Category!=NativeAot]"
dotnet test --project "$PWD/tests/OpenCode.Sdk.Tests/OpenCode.Sdk.Tests.csproj" --configuration Release --no-build -- --treenode-filter "/*/*/*/*[(Category!=ServiceElection)&(Category!=NativeAot)]"
```

The full gate runs locally only when a handoff asks for it or a CI failure needs reproducing. Any
local test run, check or gate, runs on an otherwise idle machine: several real-process tests have
wall-clock bounds, and a concurrent build, package install or second gate on the same machine can
turn them red without any change to the code. A red result is recorded before any rerun.

A change limited to Markdown, `LICENSE`, and the files under `.github/` other than build inputs
(workflows, issue and pull request templates) needs no local check.

A fresh clone or worktree is prepared once before its first local check or gate, with the Bun the
pin's `packageManager` names (`external/opencode/package.json`) first on `PATH`, for the install and
for every run after it. `dotnet tool restore` makes the repository's local tools, slopwatch among
them, available:

```bash
dotnet tool restore
git submodule update --init --depth 1 external/opencode
(cd external/opencode && bun install --frozen-lockfile --ignore-scripts)
(cd tools/jscpd && bun install --frozen-lockfile --ignore-scripts)
```

The last line installs the clone detector: `verify-clones` and the Tools clone tests run it.

`OpenCode.Sdk.Tests` checks all three before test discovery
(`tests/OpenCode.Sdk.Tests/Harness/PinnedServerPrerequisites.cs`): a missing checkout, a missing
install, or another
Bun refuses the module with one error that names each missing piece and the command that fixes it.
It refuses a run filtered to tests that start no server too: every gate and CI leg prepares the
checkout, so an unprepared one is named before it costs a run.

One exception is explicit: `OPENCODE_SDK_TESTS_BUN_MISMATCH=allow` lets a gate host whose only Bun
is another version run on it. It is for a host the maintainer has named, never a default, and CI
never sets it; `setup-pinned-server` installs and verifies the exact pin. Such a host departs from
the pin twice: its install and its gate both run on its own Bun. The frozen lockfile keeps the
installed dependency tree identical to the pin's; the Bun that runs the server is what differs. The
variable allows only a different version: a missing Bun, checkout or install is still refused, and
any other value of the variable is refused with the one it takes. Each module that runs off the pin
writes one line naming both versions, which the TRX keeps and `--output Detailed` shows, and results
from such a host state the off-pin Bun.

```bash
dotnet tool run slopwatch analyze --exclude ".scratchpad/**,external/**" --fail-on warning
dotnet build --configuration Release
dotnet format whitespace --verify-no-changes --no-restore
dotnet format style --verify-no-changes --no-restore --severity warn
dotnet test --configuration Release --no-build -- --treenode-filter "/*/*/*/*[(Category!=ServiceElection)&(Category!=NativeAot)]"
dotnet test --project "$PWD/tests/OpenCode.Sdk.Tests/OpenCode.Sdk.Tests.csproj" --configuration Release --no-build --max-parallel-test-modules 1 -- --treenode-filter "/*/*/*/*[Category=ServiceElection]"
dotnet test --project "$PWD/tests/OpenCode.Sdk.Tools.Tests/OpenCode.Sdk.Tools.Tests.csproj" --configuration Release --no-build --max-parallel-test-modules 1 -- --treenode-filter "/*/*/*/*[Category=NativeAot]"
```

The project path is absolute because the .NET 11 release-candidate SDK resolves a relative
`--project` path a second time against the project's own directory when the project has more than
one target framework.

Changes to the protocol, generator, curation, or generated output also run:

```bash
dotnet run --file tools/opencode-tool.cs -- --help
dotnet run --file tools/opencode-tool.cs -- generate --verify
```

A current development handoff may require the Unix direct-invocation smoke
`./tools/opencode-tool.cs --help` in addition to the cross-platform form above.

Slopwatch excludes local scratch work and checked-out upstream submodules because neither is
repository-authored product surface. A failed, skipped, weakened, or unrun gate must be reported
honestly.

Agent sessions also get Slopwatch's verdict while they edit. A Claude Code `PostToolUse` hook in
`.claude/settings.json` and an opencode plugin in `.opencode/plugins/slopwatch-gate.ts` both run
the manifest's Slopwatch in `--hook` mode after every file edit, and hand its report to the model.
That report covers every uncommitted change, so an earlier violation keeps reappearing until it is
fixed. Neither one blocks the edit or replaces the gate above.

The build is the semantic analyzer wall: compiler diagnostics, build-enforceable IDE rules, SDK CA
rules, third-party analyzers, and source-generator diagnostics run there for every target
compilation. The local gate builds with the wall on. CI runs it once, in the lint job's Linux
build, which sets `IncludeNet472Tests` so the Windows-only net472 test targets are analyzed too;
the three OS legs build with `RunAnalyzers=false`. Every OS compiles the same LF-normalized
sources for the same targets, apart from those net472 test targets, which are the only
OS-conditioned setting that reaches a compilation, and analyzers were three quarters of each leg's
build.
The whitespace pass separately retains UTF-8, LF, final-newline, trailing-whitespace, and
syntax-whitespace enforcement. The warning-level style pass retains deterministic import
organization and every configured Roslyn style error, including rules classified
`EnforceOnBuild.Never`, without rerunning the SDK CA and third-party analyzer set. It deliberately
uses no diagnostic allow-list so future warning/error style rules remain covered automatically.
Repository generation is a separate mutating path: after writing generated source, it deliberately
runs project-scoped full format over only generator-owned paths. That canonicalization step is not
the solution-wide CI lint gate this split optimizes.

CI runs the main test pass with a measured number of test modules at once per runner: four on
Windows and Linux, three on macOS (`test-modules` in the `ci.yml` matrix).
Microsoft.Testing.Platform overlaps test modules up to the processor count by default, and each
`OpenCode.Sdk.Tests` host starts its own pinned servers, so the cap fixes the measured overlap
instead of following the runner's core count. The values come from runs paired on the same VM
(2026-09-27): Linux against two, six runs (runs 36318362319 and 36320089583); Windows at two, three
and four, nine runs (run 36342010141); macOS at three and four, six runs (run 36328076697):

- **Linux, four:** the main pass took 27% less time, and the most loaded wall-clock-bound test used
  0.28 of its bound at two and 0.36 at four.
- **Windows, four:** the main pass took 17% less time. The process-tree startup tests in
  `OpenCodeServerLifecycleTests`, whose stage bounds are 10 to 15 seconds, stayed under two
  seconds (none of 180 samples over four seconds at two, three or four), and the most loaded
  wall-clock-bound test used 0.25 of its bound at four.
- **macOS, three:** four at a time saves only about 3% (8 s) over three and costs no margin: the
  loopback `ServicePtyHandoffTests`, with a 5 s bound, used 0.51 of it at three and 0.52 at four.

The local gate on a workstation needs no cap. A verdict never rides the clock (`testing-style.md`);
a test whose hang guard a loaded host can still reach — a real server's own bound, a `[Timeout]`
measured in seconds — carries the keyless `[NotInParallel]`
(`tests/OpenCode.Sdk.Tests/Harness/ParallelConstraintKeys.cs`).

The suite runs in three passes, locally and in CI. The main pass runs every test except two
categories. The background-service election classes carry the `ServiceElection` category
(`tests/Shared/TestCategories.cs`) and run in a pass of their own, one host at a time, after every
other test. A ten-caller election starts some twenty to thirty-four source-run servers, and a timed
test in another host that ran in those seconds missed its bound: on Windows and macOS every such
failure lined up with an election in another host, and no run without that overlap failed. The
packed-consumer native AOT tests carry the `NativeAot` category (same file) and run in a pass of
their own after that: publishing the consumer runs one ILC compile that loads every core of the
runner for about a minute, which the timing-sensitive launcher tests cannot run beside, and the
pass publishes once and shares the result across its tests. TUnit's parallel keys order tests
inside one host only, so no key can keep another host's election or compile away; a pass of its own
can. Every CI pass after the first runs even when an earlier pass failed (not after a failed build
or a cancelled run), so the test report and the badge count every pass, and the later passes leave
TUnit's HTML report upload to the first, whose artifact names they would repeat.

Each CI test pass is asserted against the committed `tests/test-roster.json`: the
`verify-test-roster` tool command fails the job unless every module the roster lists for the pass
and the runner's OS has a TRX in the pass's results directory that executed at least one test, with
no unexpected or duplicate modules. The roster is deliberately explicit rather than derived — it
cannot shrink together with the coverage it guards — so adding or removing a test project or a test
target framework updates it in the same change.

## Clone gate

The detector ignores using directives; a baseline entry that matches no clone fails the gate like a new clone does.

The lint job runs `dotnet run --file tools/opencode-tool.cs -- verify-clones`: the pinned jscpd in
`tools/jscpd/`, installed with the pinned Bun from its own lockfile, at a 50-token minimum over four
scopes. Hand-written `src/`, `tools/` and `tests/` are gated against the committed baselines in
`tools/jscpd/`: a clone a baseline does not hold fails the job, `tools/` also fails above 3%
duplicated lines, and `tests/` above 7%. The tests threshold only falls: a change that removes
test clones lowers it to its new measurement, rounded up to the next 0.5%. Generated output is
reported, not gated. The generator's manifest decides which `src/` files are generated, so the split
needs no path convention. A failure names each new clone with its
two `file:line` ranges and its size in tokens, and the job keeps the jscpd reports as an artifact. Locally the same command runs from the
repository root once `bun install --frozen-lockfile --ignore-scripts` has run in `tools/jscpd/`, and the
local check above runs it.

`verify-clones --update-baseline` rewrites the baselines, mechanically. A pull request whose baseline
gains a fingerprint or a higher count accounts for each gain in its body, the same way a new
suppression is called out. A duplicate that existed before and that the change only moved, renamed
or re-tokened maps to the old fingerprint it replaces, with the fragments' locations before and
after. Any other gain is a new clone: the body names the reason it stays and the change that will
remove it. A shrinking baseline needs no reason.

## Package compatibility

The lint job packs both packages with validation on for every code pull request, and the release
workflow's pack is the final check. Cross-asset validation checks compatible target frameworks even
without a baseline. From `1.0.0-rc.2`, the same packs also compare against the previous published
release, named by `PackageValidationBaselineVersion` in `Directory.Build.props`; previews and
`1.0.0-rc.1` have no baseline. The Tools suppression test enforces ADR-0034: patch steps accept no
breaks, minor steps accept only generated-type breaks, and major steps may break the hand-written
surface; cross-asset suppressions are never accepted. The local gate does not pack with validation:
the packed-consumer fixture turns it off, so this check never needs nuget.org locally.

## Distributed-build consumer leg

Every gate above exercises the server built from the pinned commit: the `external/opencode`
submodule, run from source under bun. `.github/workflows/consumer-leg.yml` runs the same suite
against the build upstream actually publishes for that pin - `npm install -g @opencode/cli` at the
version named by the identity table in [`spec/SNAPSHOT.md`](../../spec/SNAPSHOT.md) - on
`ubuntu-24.04` and `windows-latest`. It converts "built from the pinned commit" into "tested
against the distributed build", and it is the only mechanism that notices an operation upstream
removed from a published build without anyone looking.

The mechanism is one environment variable, not a second harness. `OPENCODE_SDK_TESTS_SERVER_COMMAND`
(`|`-separated, for example `opencode|serve`) replaces the command the exact-pin fixture starts and
changes nothing else: the same hermetic boundary (`testing-style.md`), the same seeded
repository-owned RPC plugin, the same launcher-owned readiness and teardown, the same retained
logs. The executable is resolved by the
shipped launcher from `PATH` (with `PATHEXT`, which is what starts an npm `.cmd` shim), so the lane
also re-proves that resolution on every run. The job therefore installs the build, asserts
`opencode --version`, and runs the whole `OpenCode.Sdk.Tests` project on `net10.0` plus the
sandbox's standalone launcher demo; a run whose summary is missing, or whose executed-test count
falls under the job's floor, fails on the count, because an empty run must not read as a pass.

Bun, the pinned ripgrep, and the pinned server's dependencies come from the shared composite action
`.github/actions/setup-pinned-server`, which CI uses too: this lane still runs the simulated-server
fixtures from source, and the two lanes must not drift. The background-service fixtures are also
always the pinned source: the managed service behind the discovery, stop, and Ensure-reuse live
tests starts the submodule CLI's `serve --service`, the Ensure live tests' forwarding shim runs the
submodule CLI, and neither takes the command override, so those proofs hold for the pinned commit
on this lane. The one exception is deliberate: an Ensure live test reads the override, starts the
distributed build through `EnsureAsync` on its release channel under isolated roots, proves the
election against the published CLI, and ends it through `StopAsync`; without the override it
records that it belongs to this lane.

It is `workflow_dispatch` (with an input that overrides the pinned version, for trying a candidate
build) plus weekly, deliberately not a pull-request gate: it downloads a platform CLI build per run
and depends on the npm registry being reachable. Two moments need a dispatched run with the version
input left empty, so that it tests the pin. The run tests the commit it is dispatched from:
- after a pin refresh merges, dispatch it from `master`; a red run is a finding, recorded before
  other work lands on the new pin;
- before a NuGet.org release, dispatch it from a ref at the commit the release packs, and check
  that the run's commit is the release's `commit` input; the release waits for that run to be green.

Claim discipline follows it. "Tested against the distributed build" is true only of a version this
lane has actually run; for every other published build the honest claim remains "built from the
same upstream commit this SDK pins".

## Performance

Performance is a standing concern weighted by artifact. Shipped hot paths target speed and zero
avoidable allocation; tooling also avoids obvious redundant parsing, copying, buffering, and
enumeration. Do not speculate: performance claims are settled by benchmarks in
`tests/OpenCode.Sdk.Performance.Tests` with `MemoryDiagnoser` enabled.

Performance changes carry exact before/after evidence. Intentional contract costs, such as raw body
retention on API errors, are not disguised as optimization opportunities.

The permanent suite reports exact allocated bytes (not rounded KiB) beside each case's wire bytes,
item count, payload bytes per item, allocated bytes per item, and allocation amplification
(`allocated / wire`), and exports the full BenchmarkDotNet JSON; a claim quotes those columns, not a
rounded `KB`. Each benchmark class owns one operation family and decomposes it as a component ladder
(complete operation, pipeline without materialization, generated adapter, source-generated
materialization) over wire-shaped fixtures whose sizes scale from the common case to very large, so
a regression attributes to a component rather than merely registering end to end. Every
`GlobalSetup` refuses a fixture that does not materialize the generated type it claims to measure.
Benchmark cadence follows scope. An increment-level check is targeted and cheap: filter to the
component ladders the change touches and run `--job short` (`--job Dry` first when fixtures or
benchmark code changed); its exact allocation columns are the before/after comparison, and its
timings are indicative only — never quoted as evidence. The full suite under the default job runs
as milestone evidence when an arc of work completes, from a clean copy outside the repository
(BenchmarkDotNet locates the project by name from the solution root and refuses duplicate copies
such as scratch clones). Whatever the tier, before/after runs stay within one environment, and
allocation is the primary comparison. The performance project adds a net472 target on Windows only;
net472 numbers come from the `--runtimes` net472 leg of the canonical invocation below, never from
net10 or source-equivalent probes.

The canonical comparable run, and the comparison it feeds:

```bash
dotnet run --project tests/OpenCode.Sdk.Performance.Tests -c Release -f net10.0 -- --filter 'OpenCode.Sdk.Performance.Tests.Benchmarks.*' --runtimes net10.0 net472 net11.0 --artifacts .benchmarks/<run-name>
dotnet run --file tools/opencode-tool.cs -- compare-benchmarks .benchmarks/<before-run> .benchmarks/<after-run> --output .benchmarks/<comparison>.csv
```

The generator's own benchmarks (`OpenCode.Sdk.Performance.Tests.Generator`) measure the repository
tool, which targets `net10.0` only, so they run as a separate invocation; the SDK filter above keeps
them out of the `net472` leg, whose generated runner could not compile them. Their run rows rewrite
the repository's generated tree, so they refuse to start unless the tree is clean and
`OPENCODE_SDK_BENCHMARKS_REWRITE_TREE=allow` is set: run them in a disposable clone, never in a
working checkout.

```bash
OPENCODE_SDK_BENCHMARKS_REWRITE_TREE=allow dotnet run --project tests/OpenCode.Sdk.Performance.Tests -c Release -f net10.0 -- --filter 'OpenCode.Sdk.Performance.Tests.Generator.*' --artifacts .benchmarks/<run-name>-generator
```

The generator invocation passes no `--runtimes`, because each generator class declares its own job
(the stage rows `.NET 10.0`, the run rows the Monitoring job), so every case runs once; `--runtimes`
there would add a second, default-strategy job to every class. For the SDK suite, `--runtimes` is
load-bearing: it labels each case's leg with the runtime name (`.NET 10.0`, `.NET 11.0`,
`.NET Framework 4.7.2`) in the exports, and `compare-benchmarks` joins
the two runs on exactly (case, runtime). A run launched without it is labelled by its job name
(`DefaultJob`) instead and joins nothing against the archived runs — the comparison then fails
naming both label sets, but only after the run's hours are already spent. An increment-level check
narrows `--filter` and adds `--job short`. `--artifacts` names the run's folder in the git-ignored
`.benchmarks/` store; `compare-benchmarks` reads each folder's `results/*-report-full.json`, so
those exports are the part of a run that must be preserved.
