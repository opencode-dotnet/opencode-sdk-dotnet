# 📌 Versioning and support

Date: 2026-11-23

Which .NET versions the packages support, what an older runtime costs, what a version number
promises, and which release gets fixes.

- [Supported .NET versions](#supported-net-versions)
- [Costs on the older assets](#costs-on-the-older-assets)
- [Version numbers](#version-numbers)
- [Experimental APIs](#experimental-apis)
- [Releases and support](#releases-and-support)

## Supported .NET versions

Both packages target `netstandard2.0`, `net472`, `net10.0` and `net11.0`.

| Your project runs on | What you get |
|---|---|
| .NET 10 or .NET 11 | A supported target. The SDK's runtime tests run on it on Windows, Linux, and macOS. |
| .NET Framework 4.7.2 or later | A supported target, through the `net472` asset. The SDK's runtime tests run on the `net472` target on Windows, tests that start real server processes included. The project needs a newer C# version than its default: see [.NET Framework projects](getting-started.md#net-framework-projects). |
| .NET 8 | The `netstandard2.0` asset, with a build warning. It installs and builds; it is not tested or supported on that runtime. |
| Another `netstandard2.0` platform outside .NET and .NET Framework, such as Mono or Unity | The `netstandard2.0` asset, without a warning. It is not tested or supported. |

**The rule.** The packages target every .NET release that is still in support under
[Microsoft's .NET support policy](https://dotnet.microsoft.com/platform/support/policy), plus
`net472` for .NET Framework, plus `netstandard2.0` as a compatibility asset for other runtimes. A
new .NET release becomes a target at its first go-live release candidate. A release stops being a
target no later than its end of support. .NET 8 and .NET 9 reached end of support on 2026-11-10, so
they are no longer targets.

**Why .NET Framework keeps its own asset.** The SDK needs behaviour that is specific to .NET
Framework there: the connection limits of its older HTTP stack, and stopping a whole process tree
on Windows. .NET Framework 4.7.2 also has no end-of-support date:
[Microsoft's .NET Framework support policy](https://dotnet.microsoft.com/platform/support/policy/dotnet-framework)
defines .NET Framework as a component of Windows, with the lifecycle of the Windows version it is
installed on.

**On .NET 8.** NuGet selects the `netstandard2.0` asset. Compared with a supported target:

- the build warns that the package does not support your target framework and has not been tested
  with it; `<SuppressTfmSupportBuildWarnings>true</SuppressTfmSupportBuildWarnings>` silences the
  warning;
- the asset brings extra package dependencies that supply newer .NET APIs to older runtimes, such
  as `System.Text.Json`, `System.Memory` and `Microsoft.Bcl.AsyncInterfaces`;
- the asset misses the optimizations specific to the modern targets, which
  [Costs on the older assets](#costs-on-the-older-assets) lists, and it carries no
  `IsAotCompatible` declaration;
- no test runs on .NET 8. The tests on `net472` stand in for this asset's behaviour.

**On .NET 9.** A .NET 9 project resolves the same `netstandard2.0` asset and gets the same build
warning. Nothing in this repository builds or tests a .NET 9 project.

**On an older .NET Framework.** A project on .NET Framework below 4.7.2 that can restore the
`netstandard2.0` asset gets the same warning.

`global.json` in the repository pins the .NET SDK that builds the packages, and it is a released
SDK. It sets no requirement for a project that installs the packages.

## Costs on the older assets

On `net472` and `netstandard2.0`, some costs are larger than on modern targets:

- A response body of 64 MiB or more gets a newly allocated buffer on every call.
- Smaller bodies over 1 MiB come from a pool the SDK keeps for the life of the process, which holds
  at most about 250 MiB after large responses or requests.
- A body with no declared length uses every smaller size on the way up, so one 33 MiB body can
  leave about 126 MiB held.
- On .NET Framework, a request with more than about 21 MiB of text still allocates its buffer on
  every call.
- A request on the `netstandard2.0` asset isn't pooled at all.
- On .NET Framework, reading a JSON string over 1 MiB that contains an escape, such as a newline,
  allocates one extra array of its length each time.
- On Unix (Mono), owner-only files that discovery and `EnsureAsync` write get mode `0600` through a
  `chmod` child process rather than at creation, and a failed `chmod` fails the write.

No CI run covers .NET Framework or Mono on Unix.

## Version numbers

Each SDK release is built and tested against one opencode release. That release is the SDK's
*pin*, and [`spec/SNAPSHOT.md`](../../spec/SNAPSHOT.md) names it. The SDK builds against a snapshot
of an upstream release tag, never a live branch.

From `1.0.0`, an SDK version follows the bump type of the opencode release it pins: an upstream
patch is an SDK patch, a minor a minor, a major a major. The SDK's numbers are its own; they need
not match opencode's. This is not strict SemVer: opencode changes its API, sometimes with a break,
in its own patch releases, and the SDK follows it without a compatibility layer.

| Part of the API | Examples | When it can break |
|---|---|---|
| **The hand-written API:** construction, the launcher and the background-service APIs, errors and responses, terminal sessions, options | `OpenCodeServer`, `OpenCodeClientOptions`, `PtySession` | Only in a major. |
| **Generated protocol types:** what opencode's API document defines | `SessionCreateRequest`, `SessionTextDelta` | In any release that moves the pin, a patch included, when opencode changed them. The CHANGELOG lists each such change as **breaking** under Protocol changes. A patch or minor that keeps the pin cannot break them: package validation refuses it. |
| **Experimental APIs:** the API families opencode marks experimental | `client.Experimental`, `client.PersistentPtys` | In any release that moves the pin. |

- **One generated change needs a major:** when a pin move would move an operation between a
  collection client and its per-id client.
- **The SDK's own fixes and additions ship as a patch.**
- **The maintainer may override the bump type** by judgement, and records it in that release's
  CHANGELOG section.
- **Additions on a newer server are tolerated at run time.** An older SDK reads past an unknown
  field, an unknown enum value and an unknown union variant. A payload that opencode *changed* is a
  different case: [Streaming](streaming.md#-unknown-events-do-not-break-your-consumer) shows what
  arrives then.
- **If a tool updates your patch versions for you,** read **Protocol changes** in the
  [CHANGELOG](../../CHANGELOG.md) before you take a release that moves the pin, or pin an exact
  version. `2.0.1` is an example: a patch that moved the pin, replaced schedules with automations and
  removed two config flags.

## Experimental APIs

The first use of an experimental API is a compile error that names its diagnostic id, `OPENCODE001`
or `OPENCODE002`, until you opt in. The C# compiler of the .NET 8 SDK (Visual Studio 17.8) or later
reports it, on every target, `net472` included; older compilers ignore the attribute.
[Experimental APIs](experimental.md) lists the families, shows the opt-in, and says what
"experimental" means.

## Releases and support

Each GitHub Release publishes that version's CHANGELOG section. Only the latest release on
NuGet.org is supported. [SUPPORT.md](../../.github/SUPPORT.md) says where to ask and what to
expect.

Weekly, and on demand, the core `net10.0` tests and three playground apps (AotCli, Worker and HQ)
also run against the `@opencode/cli` release that npm publishes for the pin.
