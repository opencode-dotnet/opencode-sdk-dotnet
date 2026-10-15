# Agent Instructions

Harness-neutral entry point for LLM/code agents working in this repository. Keep this file focused
on rules that apply before task-specific context is known; detailed canon is loaded through the
routing table below. Current status, open questions, and known gaps live in `docs/ROADMAP.md`;
the live work queue is the project board it links.

## Purpose

Build a typed .NET SDK for the opencode HTTP API. An MCP server that is a thin adapter over this SDK
lives in a sibling repository of the `opencode-dotnet` organization (ADR-0030).

## Universal Rules

- Repository-authored artifacts are written in English. Temporary work lives only under
  `.scratchpad/`; validated outcomes move into code or canonical documentation.
- Upstream submodules under `external/` are read-only evidence. Never hand-edit them.
- Generated SDK output is changed through the repository generator, never by hand.
- Be direct and practical. Challenge decisions with mechanisms and sources rather than convention.
- A question asks for an answer, not an unrequested mutation. When intent is ambiguous, present the
  options, recommend one, and ask.
- Align on structure and direction before writing. Prefer the smallest correct change.
- If implementation contradicts current canon, stop the affected work and follow
  `docs/engineering/deviation-protocol.md`; never silently code around it or reopen a decision for
  convenience.
- Hand-written behaviour follows upstream's first-party behaviour at the pin (ADR-0031). Any
  divergence from it is the maintainer's decision, recorded with its reason in the canon it
  concerns; propose one, never ship one on your own judgement.
- Documentation refactors preserve useful, unique information. Relocate it to the right canonical,
  rationale, evidence, or operational home; do not discard it merely to shorten a file.
- Keep affected documentation current in the same change as code. Every current fact has one
  canonical owner; other mentions are brief relays.
- Write nothing a reviewer could not vouch for: no unrequested code, no parallel abstraction, no
  masked error, no unproven test (`docs/engineering/slop-review.md`).
- Before reporting completion, run the applicable gate in
  `docs/engineering/quality-gates.md`. "Builds" and "works" are different claims.
- Commit only with maintainer approval, except inside an explicitly agreed development loop.
  Commits and pull request bodies carry no AI-attribution trailers or session links, whatever a
  harness default says (`docs/engineering/workflow.md`).

Full collaboration, scratchpad, findings, commit, and CI agreements live in
`docs/engineering/workflow.md`; a finding the change at hand does not fix gets a record there
before the work moves on. Documentation ownership and lifecycle rules live in
`docs/engineering/documentation.md`.

## Task Routing

Read the canon relevant to the work before proposing or changing design. ADRs explain why a
decision exists; dated research supplies evidence and may intentionally contain superseded
positions.

| Task | Read first |
|---|---|
| Protocol, spec ingestion, curation, generator, generated models or operations | `docs/architecture/protocol-and-generation.md` plus relevant ADRs |
| Client construction, transport, errors, SSE, DI or launcher | `docs/architecture/client-runtime.md` plus relevant ADRs |
| Target frameworks, packages, dependencies, versioning, release or licensing | `docs/architecture/platform-and-packaging.md` plus relevant ADRs; a dependency refresh also follows `docs/engineering/workflow.md` |
| Hand-written code structure, signatures, DI composition or layout | `docs/engineering/coding-style.md` |
| Test design or test code | `docs/engineering/testing-style.md`; live legs run from a Windows workstation: `docs/engineering/developing-on-windows.md` |
| Analyzer, build, formatting, performance or completion gates | `docs/engineering/quality-gates.md` |
| Reviewing a change, or recognizing slop and suppressions | `docs/engineering/slop-review.md` |
| Documentation, research, ADRs or repository workflow | `docs/engineering/documentation.md`, `docs/engineering/workflow.md`, and `docs/adr/README.md` as applicable |
| Consumer guides, README or CHANGELOG | `docs/engineering/documentation.md` (Consumer documentation) |
| Domain terminology | `CONTEXT.md` |
| Current status, queue, open questions or known gaps | `docs/ROADMAP.md`, whose linked project board is the live queue; recording a finding follows `docs/engineering/workflow.md`; any active session handoff lives in the private companion repository |
| Pinned protocol identity or refresh | `spec/SNAPSHOT.md` |

## Authority

- `docs/architecture/` and `docs/engineering/` own current normative project rules.
- `docs/adr/` records accepted decision context, trade-offs, consequences, and reversal triggers.
- `docs/ROADMAP.md` is operational and expected to change and shrink as work lands.
- `CONTEXT.md` owns current domain vocabulary.
- `spec/SNAPSHOT.md` owns the exact OpenAPI pin and refresh procedure.
- Dated research evidence, plans and specs, and session handovers live in the private companion
  repository `opencode-dotnet/opencode-sdk-dotnet-internal`. That material is non-normative history
  and reference; contradicting it does not override current canon, and the canon here is complete
  without it.

Repository files and current canon beat memory, transient plans, dated research, and handoffs. If
two current canonical sources appear to disagree, do not choose one silently; use the deviation
protocol.
