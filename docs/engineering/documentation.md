# Documentation

Date: 2026-10-15

Canonical rules for documentation ownership, authority, lifecycle, references, and lossless
maintenance.

## Document roles

| Role | Homes | Meaning |
|---|---|---|
| Agent entry point | `AGENTS.md` | Universal operating rules and task-to-canon routing only |
| Current architecture | `docs/architecture/` | Normative current technical decisions |
| Current engineering practice | `docs/engineering/` | Normative current authoring, quality, workflow, and documentation rules |
| Decision rationale | `docs/adr/` | Accepted decision context, trade-offs, consequences, and reversal triggers |
| Domain vocabulary | `CONTEXT.md` | Current terms and explicitly avoided synonyms |
| Operational state | `docs/ROADMAP.md` | What is done, next, open, and known to be incomplete; shrinks as work lands |
| Protocol provenance | `spec/SNAPSHOT.md` | Exact upstream pin and refresh procedure |
| Consumer documentation | `README.md`, `docs/guide/`, `CHANGELOG.md` | How to use the shipped packages, complete on its own (Consumer documentation, below) |

Dated research evidence and session operations live in a private companion repository
(`opencode-dotnet/opencode-sdk-dotnet-internal`); the canonical documentation here is complete
without it.

Repository files and current canonical documents beat memory, dated research, transient plans, and
handoffs. If two current canonical sources appear to disagree, do not pick a winner silently; use
the deviation protocol.

## One canonical home

Every current fact has one canonical owner. Other appearances are short relays that link to the
owner instead of restating the full rule. Change-prone values are read from their mechanical source
instead of copied into prose.

Documentation refactors are lossless relocations, not summarization exercises. Preserve useful,
unique information in an appropriate canonical, rationale, evidence, or operational home. Delete a
passage only after proving it is an exact duplicate, obsolete without historical value, or retained
in a more authoritative source. Git history is not a substitute for accessible, useful canon.

Current-state documents describe the status quo rather than narrating their amendment history.
Dated research preserves the history and may state decisions later superseded; it must never be
used as current policy without following its links to current canon. ADRs record decision rationale,
not work-queue state.

## Dates and lifecycle

Every hand-written document under `docs/` carries a `Date:` line.

- In current-state and operational documents, `Date:` is the latest substantive update.
- In ADRs, `Date:` is the date of the decision represented by the current record; a material
  decision revision moves it, while an editorial link correction does not.
- Where a `Date:` records a historical snapshot rather than a current state, an editorial
  correction does not rewrite it.

A sentence that must change when a task completes belongs in `docs/ROADMAP.md` or a temporary
handoff, not in evergreen architecture or engineering canon. Keep affected documentation current in
the same change as code; documentation is not follow-up work.

## References

References point one way: documentation may cite code, but code artifacts never cite documentation.
Comments in source, project files, `.editorconfig`, workflows, and generated files explain the
status quo locally; they do not point to movable prose or narrate decision history.

Canonical documents — architecture, engineering, ADRs, and `CONTEXT.md` — never point at the
private companion repository's paths or numbering (research-log questions, sessions, document
numbers) or at `docs/ROADMAP.md` content, which moves as work lands. Dated evidence is cited inline
by date and title, for example: internal research, 2026-08-24, "Runtime pipeline architecture
design". Naming the ROADMAP as the home of operational state, as the table above and `AGENTS.md`
do, is routing, not a reference.

Audience decides placement. Knowledge for consumers of the packages belongs in the consumer
documentation; knowledge shared by maintainers and agents belongs in architecture, engineering,
ADR, domain, or operational documents; guidance needed only by coding agents belongs in
`AGENTS.md` or the private companion repository.

ADRs are created lazily under the criteria in `docs/adr/README.md`. Handovers are consumed against
live Git and GitHub, then deleted when the follow-up ships or a newer handoff supersedes them.

## Consumer documentation

`README.md`, `docs/guide/` and `CHANGELOG.md` speak to people using the packages.

- A change that alters what a consumer sees updates the guide or README section that describes it,
  in the same pull request.
- `CHANGELOG.md` gets an entry under `[Unreleased]` for every user-facing change, in the same pull
  request. A released section is the delta from the previous published package: it states the
  status quo a consumer upgrading from that package meets, never the order of intermediate commits
  or nightlies.
- Consumer documentation names upstream where the server's behaviour matters, but never sends a
  reader to upstream's code, issues or documentation to learn how the SDK behaves; what a consumer
  needs is stated here.
