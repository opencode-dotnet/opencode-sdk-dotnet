# Snapshot production is receipt-governed; Restore patches temporarily repair projection loss

Date: 2026-08-26

The accepted snapshot is one reviewed protocol identity: an exact upstream commit, the
`spec/openapi.json` digest, an ordered production recipe whose patch list is normally empty, a
sorted operation-set digest, and the matching upstream submodule gitlink. Production always runs
the exact pinned upstream generator over the pinned source; upstream's committed artifact is read
only to record whether it matches that generator, because upstream's CI never regenerates it and it
has lagged its source: at `v2.0.22` it lacked four operations the generator emits and listed a
query parameter under the wrong operation, and at `v2.0.18` it lacked two (reported as
[anomalyco/opencode#53105](https://github.com/anomalyco/opencode/issues/53105)). When upstream's
OpenAPI projection loses content its machine-readable contract still carries — the effect beta.107 regen deleted both SSE
payload trees (reported as
[anomalyco/opencode#44911](https://github.com/anomalyco/opencode/issues/44911), restore offered as
[PR #45182](https://github.com/anomalyco/opencode/pull/45182)) — production may temporarily apply
**Restore** patches: ordered, hash-verified source patches run through the exact pinned upstream
generator. Every patch requires an upstream issue or PR, byte hashes and touched-file preimages, a
repair predicate, and a retirement predicate; when the unpatched generator's output satisfies the
repair predicate, the synchronizer refuses the patch and forces an empty-patch retirement refresh. Apply is always a human
act over one reviewed receipt.

Enrichment is forbidden: auth behavior, location headers, fixed ticket values, WebSocket framing,
and every other server-only fact stay hand-written runtime behavior and assurance evidence. Identity
and naming defects are not patched either — they ride reason-bearing operation-identity curation
rows (ADR-0013). ADR-0013 governs what generation consumes; this record governs how the consumed
document is produced. Once prepare/verify/apply tooling exists, the standing cadence is a refresh to
the latest resolved upstream commit (plus active patches) at the start of every working session,
each through its own receipt; until then refreshes remain deliberate maintainer acts. Observation
lanes never mutate accepted state, run upstream from git source at resolved SHAs through a pinned
bun toolchain with install scripts disabled, and never install upstream npm artifacts.

## Considered options

- Copy upstream's committed artifact when no patch is active, and run the generator only for
  patches — rejected: the copy inherits whatever drift upstream's CI lets through, silently, and a
  repair predicate read from that copy can retire a patch the generator still needs or keep one it
  no longer does.
- Wait for upstream to fix each projection bug — leaves shipped surface blocked indefinitely on an
  external timeline (#56 held the pin at `a6a712a3` for exactly this reason).
- Fork the document or keep standing local overrides — unbounded drift with no force toward
  retirement.
- Enrich curation from implementation source — the hidden second contract ADR-0013 exists to
  prevent.

## Consequences

- The desired patch lifecycle is empty → temporary repair → empty; a growing patch list is a defect
  signal, not a workflow.
- Synchronizer mechanics (prepare/verify/apply, receipts, observation lanes) enter
  `docs/architecture/protocol-and-generation.md` with their implementing increments;
  `spec/SNAPSHOT.md` remains the identity owner.

Evidence: internal research, 2026-08-26, "OpenAPI projection fidelity: what the document does not
carry, at two fixed points", and 2026-08-25, "Why did the spec refresh stop, and is the lost stream
surface recoverable upstream?"; for generator-only production, internal research, 2026-10-04, "What
does the `v2.0.22` refresh change under the hand-written doors, and can the snapshot trust
upstream's committed document?".
