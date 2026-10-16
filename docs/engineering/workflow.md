# Repository Workflow

Date: 2026-10-16

Canonical working agreements for research, implementation, temporary work, verification, and Git
history.

## Collaboration

- Be direct, practical, and clear. Challenge decisions with mechanisms and sources rather than
  convention; do not agree your way into a weaker architecture.
- A question asks for an answer, not an unrequested mutation. When a request is ambiguous, present
  the options, recommend one, and ask.
- Align on structure and direction before writing. Prefer the smallest correct change over a broad
  refactor.
- When implementation contradicts current canon, stop the affected work and follow
  `deviation-protocol.md`; never silently code around the contradiction.

## Repository artifacts and temporary work

All repository-authored artifacts are written in English.

Everything temporary - prototypes, scratch scripts, generated probes, and working notes - lives
under the fully gitignored root `.scratchpad/` directory. Nothing permanent references it. Keep its
minimal `Directory.Build.props` stub as an empty project that disables central package management, so
scratch projects do not inherit the repository's strict build infrastructure. Validated outcomes
move into code or canonical documentation.

Upstream submodules under `external/` are read-only evidence. Never hand-edit them.

## Verification and generated output

Verification requirements live in `quality-gates.md`, including which changes need the full gate and
which need only green CI. Run them before claiming completion and state honestly what was not run. Generated SDK output changes through the generator and is reviewed as a
complete diff; it is never hand-edited.

## Findings

A finding that the change at hand does not fix gets a record before the work moves on. A product,
test, or documentation finding becomes an issue on the project board, in a milestone or, for the
backlog, in none; a finding about process alone is recorded in the private companion repository. A
declined finding is recorded too, with its reason, in its issue or as an issue closed as not
planned. Scratch notes, session messages, and review reports are evidence, not records.

## Dependency refresh

Versions live in `Directory.Packages.props`, the SDK in `global.json`, and repository tools in
`dotnet-tools.json`. A refresh takes the latest stable versions; a prerelease arrives only with the
work that needs it. It lands as one pull request with one commit per class, in this order, so a
class can be reverted alone, and the pull request passes the full completion gate:

1. **Servicing and shipped dependencies** — the SDK feature band, the runtime's servicing packages,
   and Polyfill. The CHANGELOG names every raised dependency floor of a shipped package and the
   security fixes it carries; Polyfill's new members are audited against `BannedSymbols.txt`.
2. **Generator and tooling** — `generate --verify` and `refresh-spec --verify` stay byte-identical.
3. **Analyzers** — every new diagnostic is decided per rule under the analyzer policy in
   `quality-gates.md`, never by a blanket suppression.
4. **Test stack** — last, so a test-host regression is the last commit; a snapshot format change
   is reviewed as a complete diff. Verify's open-source maintenance-fee exemption in
   `tests/Directory.Build.props` runs at most 12 months, and every refresh moves its end month.

## Documentation sessions

Research and decision sessions end with a documentation pass. Current canon or ADR updates when a
decision changes, and operational state in the roadmap, land here as one coherent commit after
maintainer approval. The chronological research log (question -> finding -> decision) and topic
evidence go to the private companion repository, and canon cites them by date and title
(`documentation.md`).

Documentation maintenance follows `documentation.md`, especially its lossless-relocation and
single-owner rules.

## Commits and CI

- Commit only with maintainer approval, except inside an explicitly agreed development loop where
  committing is part of the flow.
- Use Conventional Commits: `feat`, `fix`, `perf`, `docs`, `test`, `refactor`, `build`, `ci`, or
  `chore`. Commits and pull request descriptions carry no AI-attribution trailers or footers
  (session links, co-author lines, "generated with" notes), even when a tool adds them by default.
- `perf` is distinct from `refactor` because performance work requires before/after benchmark
  evidence.
- A pull request opens once its change is reviewed and, where `quality-gates.md` asks for the
  local gate, has passed it, so the CI the pull request triggers runs on finished work.
- Merge only a head that is rebased onto current `master` and has passed the CI it triggers. If
  `master` moved after that run, rebase and run CI again: the repository guards count exactly over
  every test file, so a green result from an older base can turn `master` red. A documentation-only
  follow-up under the `[skip ci]` rule below keeps the evidence of the tested commit it follows.
  After the merge, the work is done when the CI the merge commit triggers on `master` is green.
- Everything lands through a pull request, except a release: its changelog cut and its
  post-publish note are Markdown-only commits pushed straight to `master` with `[skip ci]`, on the
  maintainer's go for that release.
- A documentation-only follow-up to an already-green mixed pull request may use `[skip ci]` only
  when every change since the tested commit is Markdown or `LICENSE`. Never skip CI for source,
  tests, project/build files, tool manifests or baselines, workflows, or generated artifacts.
