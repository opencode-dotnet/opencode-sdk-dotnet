# Platform and Packaging Architecture

Date: 2026-09-29

Canonical current rules for target frameworks, package boundaries, repository shape, versioning,
distribution, dependencies, and licensing.

## Target frameworks

The package matrix follows Microsoft's support lifecycle: every modern .NET version Microsoft
supports, plus `net472` and `netstandard2.0` (ADR-0002). It is:

```text
netstandard2.0;net472;net8.0;net9.0;net10.0
```

A version joins at its first go-live release candidate and leaves no later than its end of
support; a non-preview package is built only on a GA SDK. `net11.0` joins on its go-live release
candidate and `net8.0` and `net9.0` leave with it, ahead of their end of support on 2026-11-10.

`net472` owns .NET Framework-specific compile and runtime behavior. `netstandard2.0` is the broad
compatibility bridge and has no runtime of its own; net472 legs proxy its downlevel behavior. It is
a compatibility asset, not a supported runtime: a consumer on another runtime can install it, but
support and testing cover the targeted runtimes only.

Modern C# on downlevel targets is deliberate and supported inside this repository by the private,
source-only Polyfill package. Exact package versions belong to `Directory.Packages.props`, not this
document. A polyfilled member whose downlevel behaviour is a hazard is banned in
`BannedSymbols.txt`, which names its replacement. `CancellationTokenSource.CancelAsync` is one: below
.NET 8 it spins the calling worker until a queued `Cancel` starts, and concurrent callers stall the
thread pool, so product and test code cancel through the internal `CancelOnWorkerAsync`.

### WebSocket support

`ClientWebSocket` backs the PTY session door (`client-runtime.md`). It resolves on
`netstandard2.0` and on `net472` from the platform's own reference set, so **no package reference
is added for it** on any target — the dependency rule below is satisfied without a new dependency.

`net472` is the constrained leg: its `ClientWebSocket` is a thin wrapper over the operating
system's WebSocket stack and therefore requires **Windows 8 / Windows Server 2012 or later**. A
PTY session on an older Windows fails at connect rather than degrading. CI's Windows leg covers
that target.

Two separate API vintages shape the downlevel legs, and conflating them would misdate both:

- The `Memory`-based receive and send overloads arrived with .NET Core 2.1 and are absent from
  `netstandard2.0` and `net472`, so those legs use the `ArraySegment` overloads. This is a
  buffer-shape difference only.
- `ClientWebSocketOptions.CollectHttpResponseDetails` and `ClientWebSocket.HttpStatusCode` — the
  pair that recovers a refused upgrade's HTTP status — are .NET 7 and later. Both downlevel legs
  therefore report a failed upgrade without its status (`client-runtime.md`).

## Packages

- `OpenCode.Sdk` is the core typed client, and the local server launcher ships inside it
  (ADR-0001).
- `OpenCode.Sdk.Extensions` owns dependency-injection registration. DI dependencies do not enter the
  core package.
- The two names above are assembly and namespace names. The published package ids are
  `OpenCodeDotNet.Sdk` and `OpenCodeDotNet.Sdk.Extensions`, because nuget.org reserves the
  `OpenCode.` prefix for an unrelated owner. Each packable project states its own `PackageId`, and
  a project reference packs as a dependency on the referenced project's package id; the
  assemblies, the public namespaces, and therefore consumer source are unaffected by the ids. The
  manual publish lane takes the commit to pack as a required input, refuses a commit that `master`
  does not contain, and reads both ids back from the packages before any push. The earlier ids,
  `OpenCodeAI.Sdk` and `OpenCodeAI.Sdk.Extensions`, receive no further versions.
- Exact package references and dependency versions are read from project files and
  `Directory.Packages.props`. Documentation records policy, not a second version inventory.
- A future package is added only for a real distribution boundary; repository layout alone does
  not justify another artifact.

## Dependencies

- Terminal sessions use `System.Threading.Channels` for their internal receive queue. The
  SDK declares that bridge package directly for `net472` and `netstandard2.0`; modern targets
  use the inbox implementation. Channel types are not part of the public SDK API.

- Declare explicitly every package this repository's source uses directly, that appears on a
  public surface, or that is version-pinned for behavior; trust the transitive graph otherwise.
- Downlevel bridge packages (`System.Memory`, `System.Buffers`, `System.Collections.Immutable`,
  `Microsoft.Bcl.*`) are conditioned to the target frameworks that need them; modern targets use
  the inbox APIs.
- A new shipped dependency is a maintainer decision recorded with its consumer; no package is
  added for a capability no scheduled work consumes.

## Repository and versioning

The MCP server lives in a sibling repository of the `opencode-dotnet` organization and consumes the
SDK as a published package; it remains a thin adapter over the SDK (ADR-0030).

Every package versions independently. Package versions do not align with upstream opencode and do
not move in lockstep with one another. Intra-repository compatibility uses ordinary NuGet dependency
ranges (ADR-0006).

The release policy is per-merge publication to GitHub Packages and a manual pipeline for NuGet.org.
Pre-1.0 numbering is an operational decision, not a canon rule (ADR-0006).

## Licensing

Repository packages use the MIT license and ship it through `PackageLicenseFile`. Project and pack
files own the mechanical packaging configuration.
