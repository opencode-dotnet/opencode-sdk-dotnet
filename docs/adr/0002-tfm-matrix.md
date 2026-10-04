# TFM matrix follows Microsoft's support lifecycle, plus net472 and netstandard2.0

Date: 2026-09-29

The packages target every modern .NET version Microsoft supports, LTS and STS alike, plus `net472`
and `netstandard2.0`. A version joins the matrix at its first go-live release candidate and leaves
no later than its end of support; the preview line may drop it earlier. A non-preview package
(`1.0.0-rc.N` or stable) is built only on a GA SDK. For 2026 that means `net11.0` joins on its
go-live release candidate (RC1, go-live since 2026-09-08) and `net8.0` and `net9.0` leave with it,
ahead of their end of support on 2026-11-10, giving `netstandard2.0;net472;net10.0;net11.0`.
net472 exists for Framework-exact compile paths (`#if NET472`: ServicePointManager connection
limits, process tree-kill). netstandard2.0 rides the same downlevel tax already paid for net472 and is a
compatibility asset, not a supported runtime of its own: consumers on other runtimes (Unity, Mono,
.NET versions past their support) can install it, but support and testing cover the targeted
runtimes only. The net472 CI leg is its proxy coverage. The downlevel tax is paid once via Polyfill
(source-only, internal; BCL API polyfills on top of the compiler-support attributes — chosen over
PolySharp, which covers only the latter). Microsoft's own BCL packages ship the same exact+bridge
TFM pattern. Evidence: internal research, 2026-08-08, "Does adding net472 force a netstandard2.0
target?" and "PolySharp or SimonCropp/Polyfill for the downlevel TFMs?"; for the lifecycle policy,
internal research, 2026-09-29, "What stands between the preview and 1.0, and in what order?"; for
the release-candidate rule, internal research, 2026-10-04, "How does the target-framework
transition run, and what does .NET 11 change under us?".

## Considered Options

- **Promise .NET 5–7 through the netstandard2.0 asset.** Rejected: those runtimes are past their
  support, the `System.Text.Json` and `Microsoft.Extensions` 10.x dependencies do not support them,
  and no leg tests them, so the promise would claim more than the package delivers.
- **Add a version at its GA.** Rejected: the go-live release candidate ships about two months
  earlier, and CI on the new runtime in that window surfaces its breakages before GA rather than
  on it (the .NET 11 `Process` changes the launcher's Windows pipes, for one); the GA-SDK rule
  keeps every non-preview package off a pre-GA toolchain.
- **Keep an ended version as a target until 1.0.** Rejected: a target past its support keeps a CI
  leg and a public-API check for a runtime nobody should deploy, and the lifecycle rule makes each
  change predictable instead of a yearly decision.
