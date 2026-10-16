# Cursor pagination keeps explicit pages and adds item enumeration

Date: 2026-10-16

An operation whose pinned request and response bind to the supported cursor-list dialect keeps its
generated one-page `List*Async` method and additively emits `Enumerate*Async`, which returns a
`CursorSequence<TPage, TItem>`: a lazy `IAsyncEnumerable<TItem>` whose `Pages` property replays the
same traversal as page envelopes. The page method remains the `NoThrow` and `cursor.previous` path.
Automatic traversal takes `OpenCodeRequestOptions`, applies its location and network timeout to
every page request, refuses `NoThrow` at the call before enumeration, and always throws API errors
when their page is reached. A local `AsyncPageable<T>`/`Page<T>` family was rejected and stays rejected: `TPage` is the
generated response envelope itself, including the bidirectional `ListCursor`, so the page door
introduces no second public page vocabulary and no integer page-size contract absent from the pin.

The first request is sent unchanged. A continuation is that same request with first-page-only
`order` dropped and the opaque returned `cursor.next` put in place, so the pinned string `limit` and
every filter the query declares travel every page; only an absent next cursor ends the sequence. A
query binds `ListRequest` by inclusion — the three admitted members must be present in their
admitted shapes, filters may ride beside them — and a spine-carrying query that declares a required
parameter is refused, because a continuation must be constructible from no request at all. Generated
adapters project each admitted operation onto one hand-written traversal core. Other pagination
dialects require their own mechanically proven binding support rather than being inferred from
descriptions or forced through `ListRequest`.

## Consequences

- The automatic sequence is finite pull-based pagination over ordinary buffered HTTP responses,
  not a server stream and not a second transport path.
- Cancellation is observed during page requests and between already-buffered items.
- `cursor.previous` and per-call `NoThrow` remain reasons to call the page method directly; page
  metadata is otherwise reachable from the automatic traversal through `Pages`, which yields the
  same envelopes the page method returns.
- Cursors are never decoded, normalized, incremented, compared, or cycle-checked (ADR-0013).
