# Coding Style — hand-written code

Date: 2026-10-18

Binding authoring style for every hand-written line in this repository — product code,
`tools/`, and tests. Generated output is governed by the generator's emitters (ADR-0003);
mechanical formatting is governed by `.editorconfig`. This document governs what neither
can see: how code is decomposed, composed, and shaped.

## 1. The central rule

**Private methods are for local mechanics and narrative flow. Code that carries
behavior, policy, an algorithm, or a concept that deserves a name is extracted into a
named collaborator class.**

Private methods are right for:

- **Narrative helpers** — keeping a public method readable as a short story.
- **Local invariants** — internal state handling no other class needs.
- **Small technical details** — repeated mechanics with no domain meaning.

Red flags that demand extraction:

1. Private methods calling each other in chains — a workflow engine hiding in a class.
2. A private method containing a business rule, a policy, or a branching algorithm.
3. Five or more parameters — the parameter list is a class asking to exist.
4. Mutating three or more fields — temporal coupling.
5. The urge to unit-test a private method independently — extract it and test the class.
6. Generic, meaningless names on privates: `Process`, `Handle`, `Do`, `Execute`, `Run`.

Extraction targets by what the code *is*: a rule → `*Policy` / `*Validator`; an
algorithm → `*Classifier` / `*Calculator` / `*Normalizer`; a traversal → `*Walker` /
`*Projector`; an assembly job → `*Builder` / `*Composer`. Name the concept, not the
mechanism. A class is a unit of *meaning* with a testable contract — not a namespace
for a pile of static steps.

## 2. Interfaces, sealing, and dependency injection

- **Interfaces exist at seams**: filesystem, process, network, console, clock,
  environment, any external system, and cross-module boundaries — the places a test
  substitutes (NSubstitute) or a composition swaps. A seam's interface lives in the
  owning slice's `Abstractions/` folder, next to what it abstracts.
- **Everything else is a `sealed class`.** Pure logic a test can construct directly
  gets no interface ceremony — no `IThing` for a single implementation nobody
  substitutes. The analyzer wall already enforces sealed-by-default (MA0053).
- **One exception: stateless policy is a `static class`.** A policy that is a pure function over
  the data it is given — no I/O, no collaborator, no mutable state — is a static class with static
  methods, because CA1822 and S2325 make a member that touches no instance state static, and an
  instance the type would never use earns nothing. The generator's binding and emission policies
  are the common case. The moment a policy needs a collaborator, I/O or state, it becomes a sealed
  class that receives its collaborators through its constructor: from the `ServiceCollection` in
  an executable, and by hand-written composition in the SDK, which takes no DI dependency
  (`../architecture/platform-and-packaging.md`).
- **Two homes are static because the language puts them there:** native interop declaration
  holders (the `LibraryImport`/`DllImport` entry points of one platform) and extension-method
  containers. They hold declarations and thin forwarding only; any policy or I/O around a native
  call lives in a sealed class behind a seam. A static class that performs I/O or takes a
  collaborator outside these two homes is a defect to fix, not a pattern to follow.
- **DI-first composition.** Every executable has exactly one composition root: a
  `ServiceCollection` that registers each collaborator behind its seam, handed to the
  CLI framework through its DI registrar. Commands and services receive dependencies
  through constructors — no service locators, no static mutable state, and no behavior on
  static classes beyond the stateless policies above. The executable's static composition-root factory is wiring only: it may
  construct/register concrete adapters but performs no I/O itself. The shape:

  ```csharp
  public static class ToolApp
  {
      public static ServiceCollection CreateServices()
      {
          var services = new ServiceCollection();
          services.AddSingleton<IFileSystem, RealFileSystem>();
          services.AddSingleton<IAnsiConsole>(AnsiConsole.Console);
          services.AddSingleton<ToolLoggingOptions>();
          services.AddLogging(logging => logging.SetMinimumLevel(LogLevel.Trace));
          services.AddSingleton<ILoggerProvider, SpectreConsoleLoggerProvider>();
          services.AddSingleton<ILoggerProvider, FileLoggerProvider>();
          services.AddSingleton<ISpecIngestion, SpecIngestion>();
          services.AddSingleton<ICommandInterceptor, GlobalOptionsInterceptor>();
          return services;
      }

      public static async Task<int> RunAsync(string[] args, CancellationToken cancellationToken = default)
      {
          using var registrar = new DependencyInjectionRegistrar(CreateServices());
          var app = new CommandApp(registrar);
          app.Configure(config => config.AddCommand<GenerateCommand>("generate"));
          return await app.RunAsync(args, cancellationToken);
      }
  }
  ```

- **CLI composition is full-battery, not skeletal.** The single root owns configuration
  and mutable invocation options where needed, every external seam (`IFileSystem`,
  `IAnsiConsole`, process/network collaborators), Microsoft.Extensions.Logging providers,
  application services, and interceptors. Tool logging uses MEL end to end: structured
  `ILogger<T>` consumption, a Spectre-backed console provider, and an optional
  Testably-backed file provider configured by global log-level/log-file settings. Tests
  start from the same production registration path and replace seams after composition;
  they never reproduce the registration list in a parallel test-only root.
- **Cross-cutting CLI options ride an interceptor** (logging verbosity, global options), never
  copy-pasted per command. The composition root registers one exception handler for refusal output
  and exit mapping; commands do not catch refusals. The file-based entry owns Ctrl+C subscription
  and forwards its cancellation token through the composition root to the CLI framework.
- **Statics never perform outside-world operations.** The composition root may instantiate
  and register adapters; actual filesystem, console, time, environment, process, and network
  work occurs only through injected seams. The TestableIO analyzer enforces the filesystem
  half mechanically.

## 3. Signature hygiene

- **No tuple returns** across class boundaries — a result worth returning is worth a
  named record. Tuples are tolerated only inside a method body.
- **No concrete collection parameters** (`List<T>`, `Dictionary<K,V>`) on non-private
  members — accept `IReadOnlyList<T>` / `IReadOnlyDictionary<K,V>` /
  `IEnumerable<T>` or a domain type. Generated wire models expose shallow `IReadOnly*`
  properties without copying caller-owned collections; collaborators that own mutable private
  state still return a snapshot or a read-only view appropriate to that ownership boundary.
- **A parameter cluster traveling together is a record asking to exist** — the same
  three values on four signatures means the domain grew a concept the code has not
  named yet.
- **Guard public inputs** with BCL throw-helpers; assert internal invariants with
  `Debug.Assert` - the repository's defensive-programming default
  (`quality-gates.md`).
- Immutability by default: records with `required`/`init`; name actual collection ownership
  rather than treating `IReadOnly*` as a deep-immutability guarantee. Generated wire models are
  shallow init-only DTOs (ADR-0004/0014); hand-written domain types choose stronger ownership only
  when a concrete consumer or concurrency boundary requires it.
- **Regular expressions:** a regex whose pattern matches in linear time and whose input is
  repository-pinned or test-local passes `Timeout.Infinite` (`matchTimeoutMilliseconds:`), or
  `Regex.InfiniteMatchTimeout` where the API takes a `TimeSpan`. A regex over untrusted input, or
  one whose pattern can backtrack superlinearly, keeps a finite timeout with its reason beside it.
  The match timeout is wall-clock: on a linear pattern over pinned input it guards nothing and fails
  on a stalled runner (#426).

## 4. Layout

- **Vertical feature slices as folders**: a feature's types live together under its
  folder (`Generator/Ingestion/`, `Generator/Binding/`, …), never in horizontal
  type-kind buckets spanning the project.
- **Within a slice, conventional groups**: `Models/` (or the slice's domain-record
  folder) for data shapes, `Abstractions/` for its seams, `Internal/` for the slice's
  implementation where the slice also has a public surface. Similar things sit together; a
  reader predicts where a type lives before opening the tree.
- **The shipped SDK slices by feature with flat namespaces.** Each client family is a
  folder at the project root holding its client, its operation records, its models under
  `Models/` and its implementation under `Internal/`; `Common/` holds the cross-family
  public vocabulary, a root `Internal/` the machinery several slices share, and `Hosting/`
  the server launcher and the background-service doors. Namespaces are API surface and
  folders are placement: a public type declares `OpenCode.Sdk` or `OpenCode.Sdk.Models`
  wherever its file lives, and a file under any `Internal/` folder is internal and declares
  `OpenCode.Sdk.Internal`, with no sub-namespaces. A fail-closed namespace-policy test
  enforces both directions for the SDK project, where IDE0130 is off, so moving a file
  never changes a namespace (ADR-0033).
- The SDK project's root `Internal/` contains area folders only; machinery used by one slice
  lives in that slice's `Internal/` folder.
- `Hosting/Internal/Platform/` is the hosting OS layer: files are placed with their OS siblings
  by OS cohesion, not by consumer count, including the OS helpers used only by the launcher.
- **A test project mirrors the layout of the project under test**: the folder path of a
  SUT predicts the folder path of its tests (`testing-style.md` owns the rule).
- File = type (MA0048) is mechanical everywhere; folder = namespace (IDE0130) is
  mechanical in every project except the SDK, whose namespace-policy test replaces it.
  This document adds the *placement* discipline analyzers cannot see.
- **Split before it hurts:** a class approaching a few hundred lines, or a folder
  flattening into dozens of siblings, is a slice asking to be verticalized. Growth is
  handled by extraction and sub-foldering at the moment of pressure, not by a rewrite
  after the pain.

## 5. Simplicity, calibrated

Take from minimalist ecosystems the product discipline — small surface, few concepts,
no speculative abstraction. Do not take their function-soup idiom into C#: this
repository writes classicist .NET — classes and interfaces, DI, detailed tests against
seams. Both failure modes are named so neither wins:

- **Private-method soup**: one giant class, dozens of private/static steps, `List`
  parameters and tuple returns threading state through a hidden workflow — untestable
  except end-to-end. A known LLM-generation failure mode; review rejects it on sight.
- **Enterprise cosplay**: interfaces, factories, and layers wrapped around code with
  one caller and one shape. Extraction needs a *reason* from §1's red flags; a seam
  needs a *substitution* someone actually performs.

## 6. Enforcement

The analyzer wall already carries part of this document: MA0051 (method length), CA1501, CA1502
and CA1505 (inheritance depth, cyclomatic complexity, maintainability),
MA0048/IDE0130 (file/folder discipline), MA0053 (sealed), CA1062 (guards), the TestableIO analyzer
(I/O seam). No rule measures class length; review enforces what this document
states and the wall does not measure — and in agent-driven development, the executing
agent's plan carries these rules as constraints, so violations are defects, not style
preferences.
