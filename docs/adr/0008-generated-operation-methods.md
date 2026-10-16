# All operation methods are generated; excluded operations are fingerprint-pinned

Date: 2026-10-16

Every operation method on the generated surface is generator-emitted
as a one-line delegation into the hand-written behavior core; behavior (retry, error
mapping, `NoThrow`, telemetry) never lives in method bodies. Hand-written op methods
sit outside CI regen-verify and go silently stale as upstream moves — generated methods
turn every spec drift into a loud diff or a broken build (the earlier hand-written-surface position
of internal research, 2026-08-08, ".NET SDK design decisions: packaging, process management, TFMs"
§1, was overturned by the maintainer on this argument).
Hand-written remains the identity core: transport pipeline, SSE engine, launcher,
exception hierarchy, envelope base, options types, DI extensions. One recorded
family-ownership exception exists: the normal PTY family's public doors are hand-written
over generated internals (ADR-0021).

**Streaming operations are generated on the same rule.** An SSE endpoint is an ordinary
HTTP response the client reads incrementally, and the pinned contract declares it in
full — `text/event-stream` content, the `{id, event, data}` frame as the *decoded* envelope
(the wire omits `event:` and `id:` for an ordinary payload), and `data`'s payload through
`contentSchema`/`contentMediaType`. That completeness is a property of the accepted
snapshot, not of every upstream document — upstream's projection has demonstrably lost the
SSE payload links (internal research, 2026-08-26, "OpenAPI projection fidelity", T1), and snapshot
production repairs such loss under
ADR-0020 rather than weakening this rule. So the drift argument applies unchanged: the
stream *engine* is behavior and stays in the core, while stream *endpoints* emit as
one-line delegations into it, exactly like the one-shot surface. A stream yields
`IAsyncEnumerable<T>` rather than a response envelope, which is why its signature carries
no per-call options (ADR-0007). Upstream's own generator emits its SSE operations the same
way, over the same contract.

The radar covers both sides of the boundary: generated output is CI regen-verified
(ADR-0003); every **excluded** operation (`pty.connect`, future exclusions) is
**fingerprint-pinned** — the generator hashes the operation's full subtree (method, path,
parameters, content types, transitive schemas) into a committed manifest, and a spec
refresh that moves a pinned construct breaks the build for explicit review. Exclusion is
reserved for transports the HTTP pipeline cannot carry: `pty.connect` upgrades to
WebSocket, which leaves HTTP after the handshake and needs a different client stack
entirely. Non-JSON response bodies stay generated via a fail-closed
content-type→payload map (`application/octet-stream` → `Stream` payload on a disposable
envelope; `text/*` → `string`); an unknown content type breaks generation.

## Consequences

- The generator emits public API, so its emission rules (naming map, envelope payload
  names, guard clauses, XML docs) are review surface — curation config changes are API
  reviews.
- Bound handles are opt-in group curation: a row declares a client name, handle name,
  and required path parameter. The Binder applies one mechanical rule to that declaration:
  operations carrying the parameter emit on the handle with it partially applied;
  collection operations stay on the collection client. Groups without a handle declaration
  stay flat. Emitters never branch on operation IDs, wire group names, or concrete client
  names.
- Operation names come from a closed grammar, never from the HTTP method. The closing identifier
  segment is the verb when it is one of `create`, `get`, `list`, `remove`, `rename`, `timeout`,
  `update`; a `GET` without one is a read and names `Get<Subject>Async`; every other operation
  without one has no mechanical name and refuses generation until a reason-bearing, fail-closed
  `operationNames` row names it (`session.prompt` → `PromptAsync`, never `PostPromptAsync`). The
  HTTP method never removes an identifier segment. On a handle client the handle is the subject,
  so an empty subject stays empty (`session.GetAsync()`, not `session.GetSessionAsync()`); on a
  collection client it falls back to the family. A `list` operation pluralizes the last word of
  its nested subject or group fallback for its method and envelope payload member: append `es`
  after `s`, `x`, `z`, `ch`, or `sh`, replace consonant-plus-`y` with `ies`, and otherwise append
  `s`. A closed irregular table gives `Pty` → `Ptys` and keeps `Inbox` and `Saved` invariant.
  Request, response, and operation-derived payload type names are mechanical projections of the
  operation identity, never of an `operationNames` row; their stems retain the identity's subject
  and grammar verb (`SessionPromptResponse`, `PtyUpdateResponse`, `SessionListResponse`). A plural
  the rule cannot form or a subject the domain names differently rides the same row kind; so does
  an action outside the grammar (`event.subscribe` → `Events.SubscribeAsync`, not
  `GetSubscribeAsync`). An `operationNames` list row that restates the plural rule's output refuses
  generation with its operation identity and derived value. An `envelopePayloadNames` row that
  restates the mechanically derived payload member name refuses for every operation, not only
  lists; a SingleKey envelope derives that name from its wire member. The binder
  validates every final identifier and all collisions; emitters consume the bound name mechanically
  and never branch on an operation ID. Enum members follow the same shape: a value whose mechanical
  casing is graceless (`2026-07-28` → `Value20260728`) takes its member name from a reason-bearing
  `enumMemberNames` row (`Revision20260728`).
- This curation changes only .NET naming. It cannot repair a type, constraint, format, or
  validation missing from the pinned OpenAPI document (ADR-0013).

Evidence: internal research, 2026-08-17, "How do the two public names the projection cannot
express enter the API?".
