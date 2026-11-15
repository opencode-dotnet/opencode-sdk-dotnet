# One shared event feed per client

Date: 2026-11-15

The default Events door follows the pinned first-party shared-feed model: one lazy physical source
per root client, with an independent subscription per enumeration. A feed gate makes admission,
marker replay, selected publication, endings and source state atomic. Reference identity fences
retired generations; an observed runner ledger owns cleanup after synchronous logical detachment.
This replaces pull-only per-enumeration connections so one consumer cannot postpone idle detection
for the others, and several consumers do not duplicate wire traffic and decoding.

## Contract and trade-offs

- Each subscription allows 4,096 queued selected events. The next refused selected offer ends only
  that subscription and releases its backlog. A pending move's direct handoff takes no queue slot.
  A sole subscriber's overflow stops the source. The limit counts events, not bytes, and there is
  no subscriber-count limit.
- The latest cached `server.connected` marks admission in the feed's fan-out order, not liveness or a
  new server connection. Its Id can be shared. Earlier business events are not replayed; a later
  marker announces a possible gap. Recovery admits a replacement before refreshing, then applies
  its buffered tail idempotently and restarts if that replacement overflows. Exactly-once and
  cross-operation transactional snapshots are not promised.
- Types are copied and validated at the call and matched locally, ordinally, with markers always
  included. The source sends no Types query and only the ambient Location header. It decodes the
  whole bus once, shares event/metadata references and couples malformed unselected traffic to the
  shared attempt. Consumers treat payload graphs as read-only.
- Ordinary failures retry forever with fixed connect, idle and retry windows. Virtual snapshot and
  coalescing-watch members expose the last classified Exception, the retry counter and Idle when
  no source exists. They are not a transition log, cursor or liveness barrier. Watchers neither
  open nor retain a source.
- An escaping source/cleanup fault detaches the generation, publishes Idle and preserves each
  subscription's queued prefix before the same exception. Any still-current runner completion
  takes this path, including cancellation and unexpected normal return. New admission starts fresh.
- A settled move is immutable. A live caller token wins over unread data and every stored ending
  on later selections; otherwise a prefix precedes its first stored fault. Explicit enumerator
  disposal drains unread references and later pulls end false unless cancelled, without changing
  a move already settled. Root disposal closes admission before lifetime/transport cleanup and
  does not rewrite subscriptions already ended by source fault.
- Physical stop uses the sanctioned worker-cancellation helper. Runner and cancel completion are
  both observed before CTS disposal. Retired cancel failures are recorded and dropped; they cannot
  poison a replacement. Pipeline remains Events-neutral through its owned-resource hook.

## Considered options

- Retain pull-only connections: rejected because a held event pauses detection and every consumer
  duplicates I/O. A second opt-in feed door would leave the default first-party model inconsistent.
- Await a full queue, drop items, or use an unbounded queue: rejected because one slow subscriber
  would stall neighbours, silently lose data, or grow without its explicit overflow boundary.
- Reconnect/resignal every subscriber for a late joiner: rejected because joining would disrupt
  healthy neighbours merely to manufacture a fresh marker. Replay remains marker-only admission.
- Discard a source-fault prefix: rejected; upstream preserves that valid prefix. Preserve an earlier
  fault after explicit disposal: rejected; upstream later pulls end done after iterator return.
- Keep a minute give-up or add backoff/configurable capacities: rejected. Observable connection
  state supplies the missing failure channel, and fixed policy follows the pinned first-party model.
- Deep-copy each payload: rejected because sharing is upstream's model and copying adds whole-graph
  cost. Immutable treatment is documented rather than falsely implied by IReadOnlyDictionary.

## Divergences and reversal triggers

Retry composes inside the SDK source where upstream's shared layer and Solid split those layers.
Cancellation throws OCE, including over a fault-preserved prefix or a stored overflow ending,
because that is the SDK-wide cancellation contract; upstream abort returns done while subscribed
and leaves a stored ending unchanged after source completion or overflow. Exception-valued Error, explicit Idle and the
typed overflow exception are idiomatic .NET forms. Standard public exception constructors remain
available and Capacity always reports the SDK's bound. Current runtime canon records these and the
omitted application facilities (history, resync, lifecycle, re-resolution, batching and onActivity).
Revisit with a corresponding upstream contract change, a change to the SDK cancellation contract,
or a concrete consumer need. A one-off preference does not silently change the shared policy.

The hand-written runtime/error-channel breaks require SDK 2.0.0 under ADR-0034. This does not move
the protocol pin: opencode 2.3.0 already shipped with SDK 1.1.0.
