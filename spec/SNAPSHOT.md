# OpenAPI Snapshot

Date: 2026-10-09

`openapi.json` is the accepted snapshot of the upstream opencode OpenAPI 3.1 document
(ADR-0005). The SDK is built against this snapshot, never against a live branch; the pin is
taken at an upstream release tag. Refresh policy is receipt-governed (ADR-0020): a refresh
consumes an exact commit, normally with an empty patch list, and temporary Restore patches may
repair upstream projection loss under review receipts. `spec/receipt.json` is the committed
receipt of the current accepted snapshot; active patches live under `spec/patches/` beside
their hash-pinned manifests.

| Fact | Value |
|---|---|
| Upstream file | `packages/protocol/openapi.json` |
| Upstream release tag | `v2.0.26` |
| Commit | `9b4ec5714d481559990db0a816d5dec19541a814` |
| Upstream product channel | npm `@opencode/cli@latest` (channel `latest`); this tag published as `2.0.26`, installing the `opencode` command (plus the transitional `opencode2` alias) |

Upstream publishes from the `@opencode/cli` npm scope on the `latest` channel, versioned as
semver release tags (`v2.0.0`…). This pin is the release tag `v2.0.26`, so
`npm install -g @opencode/cli@2.0.26` installs a server built from exactly this commit; later
releases usually work but are not what this repository tests. `beta` and `dev` tags still exist
upstream but are not the release channel. The former `@opencode-ai/cli` scope is frozen: its
`next` and `latest` tags stopped at `0.0.0-beta-17823` (published 2026-08-21) and the scope
received no publish after 2026-09-07, so its `next` dist-tag never publishes again and that
install line no longer reaches a server this SDK can drive.

`openapi.json` is derived from upstream's own generator output, so upstream's MIT notice
travels with it in [`THIRD-PARTY-NOTICES.md`](../THIRD-PARTY-NOTICES.md).

Platform evidence for the OpenCode 2.x line: internal research, 2026-08-16, "The opencode v2
platform: branch, surface, architecture, distribution".

## Refresh procedure

The `refresh-spec` synchronizer owns refreshes (ADR-0020):

1. `dotnet run --file tools/opencode-tool.cs -- refresh-spec --ref <commit-ish>` prepares a
   candidate: it resolves the reference once to a full SHA and produces the normalized
   document by running the exact pinned upstream generator, over the ordered Restore patches
   when `spec/patches/` has any — never by copying upstream's committed document, which upstream
   does not regenerate in CI — writing the receipt and document to
   `.scratchpad/refresh/<sha>/` without touching accepted state. A patch whose repair
   predicate raw upstream already satisfies refuses, forcing an empty-patch retirement
   refresh.
2. Review the receipt: identity, hashes, operation delta, patch preimages.
3. `refresh-spec --apply <receipt.json>` installs `spec/openapi.json`, updates this file's
   identity table and date, moves the `external/opencode` submodule checkout to the same
   commit, re-pins `spec/source-watch.json` for any watched hash that moved, and writes
   `spec/receipt.json`; it stages and commits nothing.

Prepare also observes every entry of `spec/source-watch.json` at the candidate commit and records
its hash and anchor verdict in the receipt's `watchedSources`; verify checks those pins against the
submodule checkout and apply re-pins them, so an upstream change under a hand-written door is
reviewed before the snapshot moves.

4. Run `generate`; resolve what it reports (wall admits, curation rows) and review the
   regenerated diff. `refresh-spec --verify` reproduces the committed identity
   observationally.
