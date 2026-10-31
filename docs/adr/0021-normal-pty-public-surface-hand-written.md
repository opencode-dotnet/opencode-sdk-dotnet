# Normal PTY's public surface is hand-written over generated internals

Date: 2026-08-26

The PTY family's center of gravity is a live WebSocket session — a transport ADR-0008 already
excludes from generation — and a token handshake whose fixed `x-opencode-ticket` value exists only
in upstream implementation source, which ADR-0013 forbids importing into generation; the generated
public `PostConnectTokenAsync` shipped unusable as a result (internal research, 2026-08-26,
"OpenAPI projection fidelity", O2; live-verified 403
without the header and 200 with it). Decision: every public door of the normal PTY family is
hand-written — `PtysClient`, `PtyClient`, the token door, and the `PtySession` working object owning
the WebSocket connection — while the generator retains the internal raw clients, operation
descriptors, routes, query shapers, response adapters, and status verdicts plus the public wire
models, envelopes, and serializer metadata, so route, status, and schema drift still breaks
compilation locally. The ticket constant is applied internally and never supplied by a caller; the
family may not bypass the generic envelope machinery for represented responses. This is a bounded
family-ownership exception to ADR-0008's generated-surface rule, not a reversal of it.

**Scope (revised 2026-08-29):** the persistent PTY family (`experimental.persistentPty.*`)
follows the same ownership pattern for the same two reasons — its `connect` operation is a WebSocket
upgrade and its connect-token handshake requires the same `x-opencode-ticket` sentinel — while
its wire differs from the normal family's (binary output, JSON text control frames, framed
input, a cursor domain without a live-only mode, no pre-upgrade existence check). Both
families' public doors are hand-written over generated internal raw clients and share one
family-neutral socket core behind named decode, close, and upgrade seams.

## Considered options

- Split ownership through partial classes (generated CRUD public; hand-written session and token
  door) — rejected: it needs the same member-suppression machinery, splits the freeze review across
  two idioms inside one small family, and the generation saved is smaller than the coherence lost.
- Keep the generated token door — rejected: it ships a public method that cannot succeed.

## Consequences

- A non-additive PublicApi review (accepted pre-1.0; the first accepted refresh's question-family
  removals preceded it).
- The generator gains a curation-declared internal-raw emission mode; the emitted internal layer
  stays manifest-tracked and regen-verified like all generated output.
- ADR-0019 placement is unchanged: PTYs remain working objects with handles.

Evidence: internal research, 2026-08-26, "OpenAPI projection fidelity" (§3.1, §5), and 2026-08-25,
"What did the coverage-program grilling seal?".
