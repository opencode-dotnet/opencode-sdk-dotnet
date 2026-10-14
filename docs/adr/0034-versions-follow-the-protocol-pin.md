# From 1.0, a minor version moves the protocol pin; a major breaks the hand-written surface

Date: 2026-10-14

Upstream opencode changes its HTTP protocol, sometimes breaking it, in patch releases: `v2.0.26`
renamed a policy action, and `v2.0.29` moved `experimental.policies` to a top-level `policies`.
The SDK follows upstream and carries no compatibility layer; its promise is that the newest SDK
works with the newest opencode. Under strict SemVer every such upstream change would be a major
version of the SDK, dozens a year. From `1.0.0` the version's parts therefore say where a change
comes from:

- **patch:** fixes and additive changes of the SDK's own;
- **minor:** a move of the protocol pin (`spec/SNAPSHOT.md`), which may change the generated
  protocol surface the way upstream changed it, each change listed under the CHANGELOG's protocol
  changes;
- **major:** a breaking change of the hand-written surface: construction, the launcher and the
  background-service doors, the error and response spines, the terminal sessions, options.

A family upstream marks experimental carries .NET's `[Experimental]` and may change in any minor.
Package validation against the previous release enforces the split: the generated manifest tells
generated types from hand-written ones, so a break in a generated type is allowed in a minor and a
break in a hand-written type needs a major. Additive server changes do not break an older SDK at
run time, because unknown union arms (ADR-0009), unknown fields (ADR-0012) and unknown enum values
are tolerated.

## Considered options

- **Strict SemVer, a major for every upstream break.** Rejected: upstream breaks in patch
  releases, so the major number would carry no information beyond the refresh count.
- **Version numbers aligned with upstream's.** Rejected in ADR-0006: an SDK fix would wait for an
  upstream release, or the numbers would diverge anyway.
- **Stay below 1.0.** Rejected: `1.0.0` is the stable release consumers and the NuGet popularity
  transfer need.

## Consequences

- A consumer reads a minor as "the protocol moved": read the protocol changes before updating.
  Patch updates stay safe to take automatically.
- The pin is refreshed right before a release, so a release carries at most one minor step.

Reversal: upstream publishes a versioned protocol with compatibility guarantees of its own.
