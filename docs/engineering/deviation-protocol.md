# Deviation Protocol

Date: 2026-10-15

> *"No plan survives first contact with the enemy."* — Helmuth von Moltke

What to do when implementation reality contradicts current canon, an approved plan, or a sealed
decision.
This is the process rendering of the repo's engineering default — fail loudly rather than
guess; silent fallbacks only as explicitly recorded tolerances in current canon. An executing
agent never silently codes around a contradiction, and never silently "fixes" a sealed
design back toward convention.

## The ladder

Classify the deviation before acting. When in doubt between two levels, take the higher.

### Level 0 — Implementation detail

Current canon and the approved plan are silent, or the question sits below their resolution (a
private helper's name, test internals, a local refactor). **Decide and move on.** Note it in the
PR description only if a reviewer might wonder.

A departure from upstream's first-party behaviour at the pin is never level 0, even where canon is
silent: whether to diverge is the maintainer's decision (`client-runtime.md`, ADR-0031). Bring it
as level 2 with the evidence, the options, and a recommendation, and wait.

### Level 1 — Recorded fallback

The chosen mechanism fails, but current architecture or an ADR records a fallback or reversal
trigger for exactly this case.
**Execute the recorded fallback.** Pre-authorized — no stop needed, but the slice PR must
state that the trigger fired, and the documents the fallback touches are corrected in the
same change. Add a research-log entry when the evidence is worth keeping.

### Level 2 — Canonical correction

Reality contradicts a current non-decision claim — operational state, `CONTEXT.md`, an active
plan's binding constraint, or a verified research fact — and no fallback is recorded:
upstream behaves differently, an API does not exist, a count is wrong, a detail cannot be
built as written. **Stop the affected task** (neighboring tasks may continue if
untouched). Capture the evidence — commands, output, source references. Propose the
correction to the maintainer; on approval, the document is corrected in place and the
task resumes. Evidence worth keeping goes to the research log; a correction that does not land in
this change gets a record under the findings rule in `workflow.md`.

Vision and reference documents in the private companion repository (design explorations and
specs that no approved plan binds) are not protocol subjects: contradicting one is a level-0 note
in the PR, and the document is corrected opportunistically. A binding constraint of the plan the
maintainer approved for the current work (a named mechanism, order, or scope) is not reference
material: contradicting it is level 2, and the mechanism is never swapped without the maintainer.

### Level 3 — Locked-decision challenge

New evidence contradicts a normative architecture or engineering decision, or an ADR.
**Stop the slice.**
Bring the evidence to the maintainer; the decision is re-litigated in a research/grill
pass — from mechanisms and sources, per the working agreements. The outcome is either a
superseding/amended ADR or a recorded confirmation. Locked decisions are reopened by
evidence, never by convenience.

## Rules that apply at every level

- **Escalation path:** a subagent reports the contradiction to its orchestrating session;
  the session classifies the level and, at level 2+, stops and involves the maintainer.
  Subagents never self-resolve level 2+.
- **A deviation without a trace is a defect**, even when the deviation itself was right.
- **The docs pass is part of the deviation, not follow-up work:** whichever document
  carried the contradicted claim is corrected in the same change
  (`documentation.md`).
