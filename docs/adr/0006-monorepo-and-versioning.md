# The SDK packages version in lockstep; no package aligns with upstream versions

Date: 2026-10-14

Status: the MCP server's repository placement is ADR-0030's.

`OpenCode.Sdk` and `OpenCode.Sdk.Extensions` version in lockstep: one version source stamps both
packages, and every release ships both. Extensions is the SDK's dependency-injection door and
changes with the SDK's surface, so one number tells a consumer which pair belongs together.
Extensions depends on the SDK through an ordinary NuGet dependency on the release it ships with.
The MCP server lives in its sibling repository (ADR-0030), consumes the SDK as a published package,
and versions on its own.

No package version aligns with upstream opencode versions. Alignment would force the SDK's own
features onto patch releases; the 2.0 rename wave was absorbed by an explicit breaking major
instead (internal research, 2026-08-08, "Align NuGet versioning with upstream opencode versions?").

CD publishes nightly packages from `master` to GitHub Packages; NuGet.org releases run through a
manual pipeline.

## Considered options

- **Independent versions per package.** Rejected: a second version source and a release input per
  package for a package that ships with every SDK release, and a consumer would have to work out
  which Extensions version pairs with which SDK.
- **Version numbers aligned with upstream's.** Rejected, as above.

## Consequences

- A release stamps one version on both packages, and package validation (ADR-0034) reads that one
  version for both.
- A future package joins the lockstep unless it gets a decision of its own.

Reversal: Extensions, or a future package of this repository, needs releases on its own cadence.
