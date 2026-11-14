# Upstream's first-party client options are parity

Date: 2026-11-14

An option that upstream's first-party client declares on its client or on a single request is part
of the first-party behaviour ADR-0031 makes the SDK's default, so the SDK offers its .NET form
without waiting for a consumer to ask for it. At the pinned release its options include a
caller-supplied fetch (its transport), client-wide request headers, and per-request headers
(upstream `packages/client/src/promise/generated/client.ts` at the pin).

This supersedes part of ADR-0010. Its reversal trigger, "a concrete consumer demand that ambient
mechanisms cannot meet — not ecosystem parity alone", and its consequence that adding a composition
seam requires a concrete consumer need no longer apply to an option upstream's first-party client
declares. That client is not the ecosystem the clause set aside; it is the reference ADR-0031 names.
For any other seam, ADR-0010's trigger stands. ADR-0010's other decisions stand too: a client is
built through its options alone, so an admitted option arrives as an option and never as a new
constructor; the SDK owns its default transport; and the dependency-injection registration keeps
its shape.

The maintainer decided this on 2026-11-14, where ADR-0010 and ADR-0031 conflicted on first-party
client options: ADR-0031 wins.

## Considered options

- **Keep ADR-0010's trigger for these options.** Rejected. It makes parity wait on a consumer
  request, which contradicts ADR-0031's default and the release goal of no missing first-party
  surface.
- **Admit each option as a recorded divergence from ADR-0010.** Rejected. A divergence record
  per option would restate one rule many times; the rule itself is the decision.

## Consequences

- An option upstream's first-party client declares needs no consumer request to be admitted. It
  still needs a design and the maintainer's public names, as any hand-written surface does.
- The maintainer may still decline one. A decline is a divergence from upstream, recorded with its
  reason and reversal trigger in the canon it concerns (ADR-0031).
- A refresh that shows upstream added, changed or removed such an option is reviewed like any other
  watched first-party behaviour change: follow it, or record a divergence.
- An admitted option is a hand-written addition, so its version impact follows ADR-0034.

## Reversal trigger

Reconsider if upstream's first-party client stops being the reference for hand-written behaviour,
that is, if ADR-0031 is reversed.
