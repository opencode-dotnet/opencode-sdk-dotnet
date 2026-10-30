# Target the v2 protocol surface only

Date: 2026-10-30

opencode's active successor line (branch `v2`) serves exactly one HTTP surface — the protocol
contract of `packages/protocol` — and has deleted the legacy root block
wholesale: `packages/opencode` no longer exists there, the server's handlers mirror the 30
protocol groups 1:1, the TUI runs on the protocol-derived client, and the product ships a
v1→v2 session-history migration. This SDK therefore generates the v2 protocol surface only;
the 1.x legacy surface is never built, and the generation target moves to the `v2` branch's
`packages/protocol/openapi.json`, pinned as a snapshot under `spec/`. Public naming never bakes
"V2" into type or client names: operation identities are dotted group/action segments with no
version prefix, and the `V2Event` component identities that remain carry explicit reviewed .NET
names. This revises the 2026-08-08 both-surfaces decision in place — itself a revision of an
earlier v2-only position; the difference now is evidence, not taste: the both-surfaces premise
("the modern block does not cover today's capability; the MCP-server goal needs all of it today")
expired when the v2 surface absorbed the capability gap (`mcp`, `config`, `vcs`, `project`,
`shell`, … are protocol groups now) and upstream's investment, distribution channels, and migration
tooling all converged on v2. Evidence: internal research, 2026-08-16, "The opencode v2 platform:
branch, surface, architecture, distribution" and, 2026-08-13, "v2 retarget research"; prior dated
evidence: internal research, 2026-08-08, "Upstream v1/v2: product version vs HTTP API surface" and
"v2 → '2.0 branch' operation mapping — a genealogy correction".

## Consequences

- The legacy hub, the legacy-marked sub-surface, the 16 stripped-name collisions, and
  consumer-driven legacy testing all disappear; completing the generation profile means covering
  the single surface.
- The SDK targets the OpenCode 2.x server line; the pinned-snapshot and fail-closed refresh
  machinery (ADR-0020) carries the pin across upstream releases.
- The spec pin is a snapshot taken at an upstream release tag: refreshes stay deliberate and
  receipt-governed (ADR-0020), never HEAD-tracking.
- Operation-scoped names derive from the dotted identities directly. An `experimental.*` identity
  selects the flat ExperimentalClient with explicit route ids unless a curated longer dotted prefix
  owns it, and an identity that leaks its upstream group qualification is a reason-bearing defect
  repair, never a client-placement mechanism (ADR-0013).
