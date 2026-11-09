# From 1.0, the SDK follows the bump type of the upstream release it pins

Date: 2026-11-09

Upstream opencode changes its HTTP protocol, sometimes breaking it, within its own bump types:
`v2.0.26` renamed a policy action, and `v2.0.29` moved `experimental.policies` to a top-level
`policies`. The SDK follows upstream and carries no compatibility layer: its promise is that the
newest SDK works with the newest opencode; it mirrors the bump type, not the numbers.

From 1.0.0 the SDK follows the bump type of the upstream release it pins: an upstream patch is an
SDK patch, an upstream minor an SDK minor, an upstream major an SDK major. The SDK's own changes
apply on top: its fixes and additions are a patch, a break of the hand-written surface is a major.
The maintainer may override either way by judgement, recorded in the release's CHANGELOG. A pin move
may change generated types the way upstream changed them; package validation accepts a
generated-type break in any release that moves the pin, and the CHANGELOG lists each one as
**breaking** under protocol changes.

A major step accepts any baseline break, generated or hand-written, with or without a pin move;
refusal without a pin move applies only to patch and minor steps. The hand-written surface is
construction, the launcher and the background-service doors, the error and response spines, the
terminal sessions, options. A refresh-driven change to a family's handle-client placement, its
per-id surface (ADR-0019), needs a major while every other generated-type break follows the pin;
review enforces this exception because the suppression classifier cannot distinguish placement
changes from other generated breaks. Package validation compares `spec/receipt.json`'s
`upstreamCommit` at the release with the same file at the baseline release's tag, and the generated
manifest tells generated types from hand-written ones. A family upstream marks experimental carries
.NET's `[Experimental]` and may change in any release that moves the pin. Additive server changes do
not break an older SDK at run time, because unknown union arms (ADR-0009), unknown fields (ADR-0012)
and unknown enum values (ADR-0035) are tolerated.

## Considered options

- **A pin move is always a minor.** Rejected: a minor would say nothing about how far upstream
  moved, and an upstream patch would arrive as an SDK minor.
- **Version numbers aligned with upstream's.** Rejected in ADR-0006: an SDK fix would wait for an
  upstream release, or the numbers would diverge anyway.
- **Strict SemVer, a major for every upstream break.** Rejected: upstream breaks in patch releases,
  so the major number would carry no information beyond the refresh count.
- **Stay below 1.0.** Rejected: `1.0.0` is the stable release consumers and the NuGet popularity
  transfer need.

## Consequences

- A patch can carry generated-type breaks when upstream's patch did; the CHANGELOG's **breaking**
  marker under protocol changes identifies them, so read a release's protocol changes before
  updating.
- A hand-written break needs a major.
- The pin is refreshed right before a release, so a release carries at most one pin move.

Reversal: upstream publishes a versioned protocol with compatibility guarantees of its own.
