# Architecture Decision Records

Date: 2026-11-15

Decision records for this repository. Each ADR is a standalone file named `NNNN-slug.md`,
numbered sequentially — scan the directory for the highest number and increment. No index is
maintained here; the directory listing is the index.

Layering: `docs/architecture/` and `docs/engineering/` carry current normative rules. An ADR is
the canonical record of one accepted decision - its context, trade-off, rationale, consequences,
and reversal triggers where they exist. Dated evidence lives in the private companion repository
and is cited inline as prose, not promoted into current policy. `AGENTS.md` routes each task to
the relevant canon.

The current rule and its ADR must agree. If they appear to diverge, stop and use the deviation
protocol rather than choosing one silently. A material decision reversal updates the current canon
and either revises the existing record deliberately or supersedes it with a new ADR; git and dated
research retain the historical chain.

## When to write an ADR

All three must hold:

1. **Hard to reverse** — changing course later carries real cost.
2. **Surprising without context** — a future reader would wonder "why did they do it this way?"
3. **A real trade-off** — genuine alternatives existed and one was chosen for specific reasons.

If any leg is missing, skip the ADR: current conventions and process rules belong in the relevant
`docs/engineering/` home, current architecture in `docs/architecture/`, and dated findings in the
private companion repository.

## Format

    # {Short title of the decision}

    Date: YYYY-MM-DD

    {1-3 sentences: context, decision, why.}

That is the whole required format — an ADR can be a single paragraph. The value is in recording
*that* a decision was made and *why*, not in filling out sections. Optional sections, only when
they add real value:

- **Status** frontmatter (`proposed | accepted | deprecated | superseded by ADR-NNNN |
  superseded in part by ADR-NNNN`) — when a decision is revisited.
- **Considered Options** — when the rejected alternatives are worth remembering.
- **Consequences** — when non-obvious downstream effects need calling out.
