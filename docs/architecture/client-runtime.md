# Client Runtime Architecture

Date: 2026-09-24

Canonical current rules for client construction, transport ownership, API errors, streams, and the
local server launcher. Protocol and generated-model rules live in
`protocol-and-generation.md`.

Every behaviour in this document that the SDK implements by hand follows upstream's first-party
behaviour at the pin as a north star, with the Bun build as the reference runtime. A deliberate
divergence is either the idiomatic .NET form of the same contract, measurably safer or more
robust, or a superset of upstream's behaviour. Each one is listed, with its reason, in the
section it concerns (ADR-0031).

## Construction and transport ownership

- `OpenCodeClient(OpenCodeClientOptions)` is the only public construction path (ADR-0010).
- The SDK owns a singleton-friendly transport and disables automatic redirects on every owned
  handler. A surfaced 3xx is an undeclared protocol response, never a route, authority, method, or
  credential transition the SDK follows implicitly. Modern targets use `SocketsHttpHandler` with a
  120-second pooled connection lifetime. Downlevel targets configure the endpoint-and-proxy
  `ServicePoint` with an effectively unbounded connection limit and the same 120-second connection
  lease before construction and each owned send, avoiding both long-lived stream starvation and
  stale endpoint retention without mutating process-global defaults. The endpoint-scoped
  `ServicePoint` itself is process-shared, so this policy also governs other process traffic to the
  same endpoint-and-proxy pair (ADR-0010).
- The `(HttpClient, options)` constructor is internal friend-assembly surface for this repository's
  tests and benchmarks. There is no public transport-injection constructor (ADR-0010).
- `OpenCode.Sdk.Extensions` registers one singleton root client and resolves every sub-client from
  that instance. It does not use `IHttpClientFactory` or depend on
  `Microsoft.Extensions.Http` (ADR-0010).
- No consumer transport-composition seam is public today. Adding one requires a concrete consumer
  need and a deliberate design; omission is reversible because a future public seam is additive
  (ADR-0010).
- Client options are snapshotted at construction. Explicit-endpoint construction reads no process
  environment variable; consumers resolve their own configuration. `OpenCodeServer.DiscoverAsync`
  and `OpenCodeServer.StopAsync` are the doors that read the environment, and they read exactly
  `XDG_STATE_HOME`, `XDG_CONFIG_HOME`, `OPENCODE_CONFIG_DIR`, and the user profile (`USERPROFILE`
  on Windows, `HOME` elsewhere), because the registration file those roots locate is that mode's
  complete endpoint-and-credential contract; they read no `OPENCODE_*` credential variable.

### Location

- `LocationSelector` addresses a directory only. `OpenCodeClientOptions.Location` is the ambient
  default, snapshotted at construction into a precomputed `x-opencode-directory` header.
- `OpenCodeRequestOptions.Location` overrides that directory per call. A set `Directory` wins;
  a null selector or directory inherits the ambient value. Blank values are refused, so a caller
  can inherit or replace the ambient directory but cannot clear it for one call.
- The directory is percent-encoded before it rides the header; the server percent-decodes it.
  This applies to both the ambient snapshot and per-call overrides, including Unicode paths.
- An operation's declared `location[directory]` query is a separate channel, emitted by
  `QueryStringBuilder.AddLocation`. It does not participate in the ambient/per-call header merge.
- The server ignores the directory header on operations that resolve location from a session
  or do not resolve location. The SDK sends it uniformly and adds no per-route validation.
- The pinned server has no workspace targeting channel, so none exists in the public selector or
  in the header/query serializers. A generated wire model may still contain a workspace ID
  when its own pinned component declares one; that is not a request-targeting channel.

## PTY family ownership

- The normal PTY family is one of the two families whose **public** surface is hand-written:
  `PtysClient` and `PtyClient` live in `src/OpenCode.Sdk/Ptys/` as ordinary hand-written code,
  and the generator emits `PtysRawClient`/`PtyRawClient` internally beside them under the
  `internalRaw` curation emission. Everything else the family needs — routes, query shapers,
  response adapters, status verdicts, wire models, envelopes, and serializer metadata — stays
  generated, so route, status, and schema drift still breaks compilation locally (ADR-0021).
- The hand-written doors keep the generated family's shape: the protected mocking constructor,
  `virtual` members, and the `MockSeam` guard. Each door delegates once into its raw twin and
  adds nothing but the knowledge generation may not import.
- `CreateConnectTokenAsync` is that knowledge. The server's connect-token handler requires a
  fixed `x-opencode-ticket` value that exists only in upstream implementation source, which
  ADR-0013 forbids importing into generation; the constant therefore lives in the hand-written
  door alone and is never a caller's argument, never in curation, and never in generated output.
  The request's `location` query — not the ambient directory header — fixes the scope the ticket is
  minted for.
- **Declared headers are the runtime channel that carries it.** An operation's document-declared
  header parameters ride `PipelineMessage.DeclaredHeaders` (`IReadOnlyList<DeclaredHeader>?`),
  written by `Pipeline` from the value the generated raw method collected and read by
  `RequestDecorationPolicy`, which adds each entry with `TryAddWithoutValidation` exactly as it
  adds the directory header. The policy never learns a family or a header name, so no operation's
  knowledge leaks into it. This is not a general header facility: the channel is assembly-internal,
  only generated internal-raw methods feed it, and only a parameter the pinned document declares
  ever becomes an entry.

### PTY WebSocket session

- `PtyClient.ConnectAsync` opens `PtySession`, the family's live working object: `ReadAsync`
  enumerates `PtyFrame` values, `WriteAsync` sends input, and `DisposeAsync` closes. The caller
  owns disposal of the session; peer closure and transport failure can also end the connection.
- **Transport divergence.** This is one of the two public SDK doors that do not ride the HTTP
  pipeline (the persistent PTY session is the other; the background-service info probe is an
  internal third). The upgrade builds its own `ClientWebSocket`, so a
  caller-supplied `HttpClient`, its proxy, its handler chain, the redirect policy, the
  pooled-connection lifetime, and the pipeline's progress window **do not apply** to a PTY session.
  What the session does inherit is the construction-time `ConnectionSnapshot` the pipeline
  publishes: the normalized endpoint, the Basic credential, and the ambient location.
- **Authentication.** The Basic credential rides the upgrade request's `Authorization` header. The
  API's authentication middleware skips credentials only for a URL carrying a non-empty `ticket`
  query, so a header-authenticated upgrade is the designed non-browser path. The SDK never mints a
  ticket for its own connection — a single-use 60-second credential in a URL that reaches logs is
  strictly worse than the header the client already holds. `CreateConnectTokenAsync` stays the
  public door for handing a browser one, and `PtyConnectOptions` deliberately has no ticket member.
- **Address.** `http`/`https` become `ws`/`wss`; the path is `/api/pty/{ptyID}/connect`. The query
  carries the merged location as `location[directory]` plus `cursor` when
  set, built through the same `QueryStringBuilder` every generated route uses. The connect scope
  must resolve identically to the scope the token door resolved.
- **Location merge.** `PtyConnectOptions.Location` merges over the ambient location member by
  member, with exactly the sealed semantics `RequestDecorationPolicy` applies on the header
  channel: per-call wins, null inherits, no clearing. `LocationMerge` states that rule once for
  the query channel; the policy keeps the equivalent fused form so the ambient directory's escape
  stays computed once.
- **Cursor.** Omitted replays the full retained buffer, `-1` attaches live-only, and a value at or
  above zero resumes from that absolute output cursor. The server accepts only JavaScript safe
  integers at or above `-1` and silently coerces anything else to omitted, so `PtyConnectOptions`
  refuses an out-of-range value rather than letting a resume become a full replay.
- **Failed upgrade.** A missing PTY answers plain HTTP 404 before upgrading; a rejected credential
  or origin answers 401/403. A failed upgrade has no response spine, so it cannot ride ADR-0007's
  envelope machinery: the transport plane is the honest channel and every case throws
  `OpenCodeTransportException` naming the PTY and the cause. Modern targets read the status from
  `ClientWebSocket.HttpStatusCode` (enabled by `CollectHttpResponseDetails`); `net472` and
  `netstandard2.0` cannot report it, so the failure names the connect context instead of guessing
  a status. `platform-and-packaging.md` owns the target-framework detail.
- **Frames.** Server output rides text frames and decodes as `PtyOutputFrame`. The one binary
  control frame is a `0x00` marker byte followed by UTF-8 JSON `{"cursor": n}`, sent once after
  replay, and decodes as `PtyCursorFrame`; a control body that is not a JSON object carrying an
  integer `cursor` is a protocol failure. A binary message that does not start with the marker is
  ordinary output. Output is decoded with **replacement**, never fatally: the server chunks its
  replay at 64Ki UTF-16 code units, so a chunk boundary can split a surrogate pair.
- **Input.** A terminal's Enter key is carriage return (`\r`); `WriteAsync` sends exactly the
  bytes it is given, so a caller that uses the raw door must end the line with `\r` — `\n` renders
  the text but never submits it on the Windows console host . `SubmitAsync` is
  the door for one command: it sends the line plus `\r` through the same serialized send path, and
  refuses a line carrying `\r` or `\n`, because one submit is one Enter and an embedded break
  would submit a command the caller did not write. An empty line is a bare Enter, and nothing else
  about the line is transformed.
- **Close.** 1000 ends the enumeration normally — the process exit code is not on this wire, so a
  reader that needs it calls `GetAsync`. 4404 means the session was not found or had already
  exited and throws with the reason; because an exited PTY still upgrades cleanly, that failure
  surfaces on the first read rather than on connect. Any other close is an abnormal close, and a
  socket fault maps through `FailureClassification`'s PTY WebSocket phases.
- **Concurrency and reading (both terminal families).** One active public read enumeration is
  allowed; a concurrent second reader throws `InvalidOperationException`. A connection-owned
  receiver assembles complete messages into an unbounded `Channel<TFrame>` independently of
  public enumeration. Canceling or disposing an enumerator ends only that consumer; another
  enumeration may continue on the same otherwise healthy connection. The queue retains only
  undelivered frames, and can grow when consumers are absent or slow. Connection termination
  delivers the valid queued prefix before normal completion or the recorded transport/protocol
  error. Later readers observe the same terminal outcome; consumer cancellation does not erase it.
  A frame already selected for delivery may win a race with cancellation.
- **Explicit disposal (both terminal families).** The session owns its socket and receiver.
  Disposal rejects new sends, wakes waiters, attempts bounded graceful close, then tears down
  the socket and awaits owned I/O completion. It releases unread queued references without
  waiting for consumer processing. Concurrent disposal calls await the same cleanup. Reads
  after disposal are empty; writes after disposal throw `ObjectDisposedException`. A pending
  read ends normally unless its own cancellation was already observed first. Expected graceful
  close failures do not prevent hard cleanup. The graceful-close bounds are separate waits
  for send admission and close-output; they are not one total budget or the send-timeout setting.
- **Send timeout (both terminal families).** `PtyConnectOptions.SendTimeout` and
  `PersistentPtyConnectOptions.SendTimeout` configure a fixed total budget for each WebSocket
  input or resize send, including serialization wait. Their default is 30 seconds. The option
  accepts 1 millisecond through `TimeSpan.FromMilliseconds(int.MaxValue)`, inclusive, is captured
  at connect, and never travels on the wire. Progress does not restart the budget. Expiry before
  socket entry fails only that call; interruption during physical send terminates the attachment
  because delivery is uncertain. Caller cancellation reports `OperationCanceledException`;
  SDK expiry reports `OpenCodeTransportException` with a timeout cause. No send is retried.
  This setting does not govern connection establishment, HTTP operations, idle time, command
  execution, or disposal.

### Persistent PTY family

- The persistent PTY family carries the same ownership: `PersistentPtysClient` and
  `PersistentPtyClient` live in `src/OpenCode.Sdk/PersistentPtys/` as hand-written code over the
  generated `PersistentPtysRawClient`/`PersistentPtyRawClient` beside them, under the same
  `internalRaw` curation emission, with routes, adapters, verdicts, wire models, envelopes, and
  serializer metadata all still generated (ADR-0021). Placement follows ADR-0019 over the group's
  `ptyID` handle parameter: the id-keyed operations sit on the bound handle, while the
  session-keyed `list`, `create`, and `read` and the unkeyed `handoff` and `shutdown` sit on the
  collection client with their route values as arguments, exactly as upstream flattens the group.
- **The doors.** `PersistentPtysClient` carries `ListPersistentPtysAsync(sessionId)`,
  `CreatePersistentPtyAsync(sessionId, request)`, `ReadAsync(sessionId, …)`, `HandoffAsync`,
  `ShutdownAsync`, and `GetPersistentPtyClient(ptyId)`; the handle carries `GetAsync`,
  `UpdateAsync`, `RemoveAsync`, `GetSnapshotAsync`,
  `CreateConnectTokenAsync`, and `ConnectAsync`. `HandoffAsync` and `ShutdownAsync` are
  server-lifecycle doors rather than terminal doors: the first prepares the daemon to outlive this
  server until a replacement claims it, the second stops the daemon and every terminal it owns.
  `CreateConnectTokenAsync` applies the `x-opencode-ticket` sentinel through the internal
  `PtyTicketHeader` both families share, since both connect-token handlers require the same value.
- **Connect and attach.** `PersistentPtyClient.ConnectAsync` opens `PersistentPtySession` and
  returns only after the server's `attached` frame, so `PersistentPtySession.Attachment` is always
  known: the attachment identity, the negotiated input protocol, the terminal as it stood, the
  granted role — which is not necessarily the role the request asked for — the resize generation,
  and the replay bounds. That frame is consumed at connect and never yielded to a read, and a
  server negotiating any input protocol but the framed one is a connect-time protocol failure. The
  upgrade is the same transport divergence the normal family's is; the address is
  `/api/experimental/persistent-pty/{ptyID}/connect` and its query carries no location, because
  this family's terminals are keyed by id alone. The wire spells exactly two roles, `controller`
  and `observer`; any other value — a JSON null included — is one of those member-level frame
  failures, never a default, because reporting an observer as a controller would have a caller
  writing input the server accepts and drops.
- **Bytes, not text.** Output rides binary messages, so `PersistentPtyOutputFrame.Data` is
  `ReadOnlyMemory<byte>` and nothing is decoded: a frame is free to split a multi-byte character,
  and a caller feeding an emulator writes the bytes as they are. A screen checkpoint — the
  terminal-escape stream that repaints a screen state — is bytes for the same reason, on
  `PersistentPtySnapshot.Checkpoint` and on the resize frame alike: base64 on the wire,
  `ReadOnlyMemory<byte>` in the SDK.
- **Frames.** `ReadAsync` yields a closed `PersistentPtyFrame` hierarchy — one named
  `PersistentPty*Frame` type each for the attached, output, replay-complete, resized, exited,
  controller-changed, title-changed, and foreground-process-changed messages — plus
  `PersistentPtyUnknownFrame`, which carries a control `type` this SDK does not know together with
  its raw body instead of failing the read, because the socket is an experimental surface that may
  grow kinds. A body that is not a JSON object carrying a string `type`, and a known frame whose
  members cannot be read, are both protocol failures, reported apart because they mean different
  things.
- **Input.** `WriteAsync(ReadOnlyMemory<byte>)`, `SubmitAsync(string)`, and
  `ResizeAsync(cols, rows)` each send one binary message in the framed input protocol's layout —
  `[type u8][cols u16 BE][rows u16 BE][data]`, type 1 for input and type 0 for a viewport change —
  which the SDK negotiates on every connection and is the only protocol it writes. `SubmitAsync`
  carries the same one-line rule both families share: it UTF-8 encodes the line plus the carriage
  return a terminal's Enter key sends into one type-1 frame, refuses a line carrying `\r` or `\n`,
  and treats an empty line as a bare Enter. The outbound viewport expresses this attachment's local
  size intent. It starts at the attachment's size, and a successful `ResizeAsync` send changes
  the size carried by later input. An inbound resize report remains available to the consumer
  for rendering but does not overwrite that local intent. Header preparation, socket send, and
  successful resize-state publication share one serialization boundary, so each input carries
  a coherent size in send order. Input from a
  connection the server attached as an observer is accepted here and dropped there.
- **Cursor.** `PersistentPtyConnectOptions.Cursor` is a relay to the connect query and nothing
  more. There is no live-only mode here: null replays from the oldest retained byte, and zero means
  the same rather than "replay nothing". The server accepts JavaScript safe integers at or above
  zero and answers HTTP 400 before the upgrade for anything else, so the option refuses an
  out-of-range value rather than spending a round trip on it. Per-frame offsets are not on this
  wire, so a resume anchors on the previous connection's replay-complete `EndOffset` or on the
  terminal's `Info.Output.Tail`; a cursor pointing at trimmed output is advanced by the server,
  which reports the gap through the attachment's replay bounds.
- **Close.** 1000 ends the enumeration normally. 4404 means the terminal does not exist **or** the
  `opencode-pty` daemon is unavailable — one application code for both causes, which a caller
  cannot tell apart from the wire — and because this family runs no pre-upgrade existence check it
  arrives before the `attached` frame, so `ConnectAsync` surfaces it rather than the first read.
  Any other close is abnormal.
- **Failed upgrade.** The connect query and the credential are checked before the upgrade: 400
  names the rejected query (the cursor is the value that can produce it), 401 and 403 name the
  rejected credential or origin, and any other status is named as the answer it was. There is
  deliberately no 404 arm even though the pinned document declares one — a missing terminal
  upgrades and then closes 4404.
- **Daemon facts a caller must know.** These terminals belong to the `opencode-pty` daemon rather
  than to the server process: the server spawns it as its own child, and the terminals survive a
  server restart through `handoff`. At the accepted pin the daemon ships darwin and linux platform
  packages only, so on any other platform `create` — the one route that starts it — answers the
  declared 503 whose `service` is `opencode-pty`, while the rest take their daemon-absent arms:
  `list` an empty list, `read` a null payload, the id-keyed reads and writes a 404 they share with
  an unknown id, `shutdown` a 204, and `connect` a 4404 close. `ShutdownAsync` ends every terminal
  the daemon owns, not one.
- **Shared core.** Both families' sessions run on the internal, family-neutral
  `TerminalSocketCore<TFrame>` — a connection-owned receiver with fragment reassembly, an
  unbounded consumer queue, serialized sends with per-call deadlines, bounded graceful close, idempotent disposal,
  and one active public read enumeration — and differ only behind
  three named seams: `ITerminalFrameDecoder<TFrame>` for what a message carries and
  `ITerminalClosePolicy` for what a close status means, both consumed by the core, and
  `ITerminalUpgradeFailurePolicy` for what a refused upgrade means, consumed by the shipped
  adapter at connect — before a core exists at all.

## Error channels

- API failures throw through `OpenCodeException` -> `OpenCodeApiException` by default. Tagged
  protocol error payloads remain generated, typed data on the exception (ADR-0007).
- A one-shot call may select per-call `NoThrow`, returning the same typed error data on its response
  spine. There is no client-level error-behavior switch (ADR-0007).
- Throwing API errors retain the raw response body. `NoThrow` responses retain it on the shared
  response spine, including when typed error parsing fails (ADR-0007). A response's printed form
  (`ToString`) leaves the raw body out, because a server can echo request data into it; the status,
  the error flag, and the typed error still print.
- Transport, status/framing, JSON, dispatch, cancellation wrapping, and impossible top-level
  materialization failures throw `OpenCodeTransportException`. `NoThrow` applies only to declared
  API errors and never suppresses transport failures. Undeclared 3xx responses are protocol
  failures on both one-shot and streaming paths (ADR-0007, ADR-0014).
- Streaming operations return a stream rather than a response envelope and therefore expose no
  per-call request-options parameter. Their failures always throw (ADR-0007).
- Network progress is bounded by an internal per-read window (100 s today): the send and every
  buffered body read must progress inside it, and each read that progresses re-arms it, so a
  slow-but-flowing body survives while a stalled one fails. A raw-byte request body is the caller's
  `Stream`: it is read once from its current position to its end, never disposed, rewound, or
  buffered whole, declares a `Content-Length` only when the stream can seek, refuses a second
  send, and restarts the window with each chunk written, so the upload is bounded by progress as
  well. The pipeline owns this timer — the
  owned transport's `HttpClient.Timeout` is infinite so two mechanisms cannot race, and a
  caller-supplied client's own timeout bounds only its send. A stalled read no token can reach is
  interrupted by disposing the content. An error body returned while opening a stream buffers
  under the same window; a successful SSE body remains live until caller cancellation, server
  completion, or failure. Caller cancellation still passes through; an exhausted progress window
  maps to `OpenCodeTransportException`.
  Successful JSON bodies materialize directly from validated UTF-8 bytes when possible. One decoding
  policy applies the modern `HttpContent` charset/BOM and malformed-UTF-8 replacement algorithm on
  every target framework and on both success and error planes. Error bodies stay decoded strings so
  throwing and `NoThrow` paths retain exact `RawBody`. Declared no-content successes drain an
  unexpected body with the buffered response and ignore it (ADR-0007, ADR-0014).
- A schema-valid reserved failure frame throws `OpenCodeStreamFailureException`, a subtype of
  `OpenCodeTransportException`, with typed causes on its non-null `Cause` collection. Invalid cause
  JSON, null materialization, and declared-but-impossible cause tags remain base transport/protocol
  failures rather than partially typed exceptions (ADR-0015).

## Server-sent events

- Streams are exposed as `IAsyncEnumerable<T>` and open lazily on first enumeration.
- Cancellation closes the stream through the enumeration token. On downlevel targets, the SDK also
  disposes the live response so cancellation interrupts platform response-stream reads that do not
  observe an async read token; disposal-induced I/O failures remain caller cancellation.
- The SDK never auto-reconnects. A live-stream consumer refreshes authoritative state and
  resubscribes after failure.
- Durable continuation is requested explicitly through `session.log`'s `after` parameter, an
  exclusive aggregate sequence read from a durable envelope or from the sync marker. One
  `log.synced` marker reports the watermark the replay was captured at; it is a boundary rather
  than a durable event, and under `follow` the events committed while that replay was in flight
  arrive after it. Sequences are not contiguous, so a gap is ordinary and no count is derived from
  two of them. A cursor past the aggregate's tail is accepted rather than refused and then
  suppresses live delivery until the aggregate overtakes it, so a cursor travels only within one
  server's lifetime.
- That log has no retention policy: nothing expires, prunes, or caps it, compaction and revert
  rewrite projections rather than the log, and rows are deleted only with their session.
  Persistence is itself a server option that is off unless the process starting the server turned it
  on, and no distributed `opencode` build exposes a way to turn it on: the CLI's serve command
  declares no such flag, bridges no environment variable to it, and reads no configuration key for
  it. The live part of `follow` reads the same persisted store as the replay, so a replay and a
  follow from such a server are both contract-valid and empty of history — one `log.synced` marker
  at the aggregate's current sequence, with no durable events before or after it — which is the
  detection signature a caller can test for. Only a host that embeds the server library with
  `events.persist` enabled replays and follows history; this repository's own simulation host is
  that host for the live tests.
  `tests/OpenCode.Sdk.Tests/Sessions/SessionLogCliProfileLiveTests.cs` holds the reversal
  triggers: when one fails, upstream changed what a CLI-started server's log delivers, and this
  sentence, `docs/guide/streaming.md`, and the README's known issue change together.
- The SSE event name is a framing signal. An ordinary payload uses the default `message` name;
  the operation's declared failure event materializes its cause through generated metadata and
  throws; any other explicit name is refused. Unknown payload and cause discriminators remain
  governed by ADR-0009 and are not confused with unknown frame names. A known tag whose schema is
  impossible is a protocol failure, not an unknown variant (ADR-0015).
- A body cut in the middle of an event is reported as a transport failure rather than dispatched
  as malformed payload data.

## Pagination

- A supported cursor-list operation has two generated doors: `List*Async` returns one endpoint-
  specific page envelope, while `Enumerate*Async` returns a `CursorSequence<TPage, TItem>` that
  lazily yields the items across pages and, through its `Pages` property, the same traversal's page
  envelopes. Explicit pages retain per-call `NoThrow`; automatic traversal never takes per-call
  options and therefore always throws API errors when their page is reached (ADR-0007, ADR-0017).
- `CursorSequence<TPage, TItem>` is a recipe, not a buffer: the item door and the page door each
  drive the one traversal core independently, so enumerating either performs its own requests, and
  enumerating both performs both walks. Its `TPage` is the generated response type — no page
  vocabulary and no page-size contract is introduced. The token given to `Enumerate*Async` and the
  token given to `GetAsyncEnumerator`/`WithCancellation` are both observed.
- `ListRequest` carries the pinned string `limit`, first-page `order`, and opaque `cursor` channels,
  and a derived request may declare its own filters beside them; `ListCursor` preserves the
  response's optional `previous` and `next` values. The first automatic request is sent unchanged,
  including an order-plus-cursor pair. Each continuation is that request again with `order` dropped
  and the returned `cursor.next` put in place, so `limit` and every filter travel every page and a
  filter added to the pinned query needs no generator change.
- A missing `next` cursor is the only normal end signal. An empty page with `next` continues;
  `previous` remains available through explicit page calls. Cursor values are never normalized,
  incremented, compared, or cycle-checked. Cancellation reaches each request and is checked between
  buffered items.
- Automatic pagination is a finite pull sequence over ordinary buffered HTTP calls, not SSE. A
  different pagination dialect does not inherit these rules from naming or prose; it requires a
  mechanically proven binding of its own (ADR-0013, ADR-0017).

## Launcher

`OpenCodeServer.StartAsync(OpenCodeServerOptions?, CancellationToken)` is the standalone door
(upstream `Standalone.start` parity), hand-written over `System.Diagnostics.Process` with no
process-management dependency (ADR-0001). Every call is always a fresh private server on port
zero: the caller's `Command` — `opencode serve` by default, the command the `@opencode/cli`
package installs (the package also installs a transitional `opencode2` alias pointing at the same
executable) — plus `--stdio --port 0` is the argv, and a freshly generated lease
credential is injected into the child environment as `OPENCODE_PASSWORD`, after any caller-supplied
`Environment` entries so it can never be shadowed. Readiness is the single JSON stdout line the
child prints once fully booted; stdin stays open as the ownership lease for as long as the server
runs, and every later stdout line plus all of stderr is drained continuously (stderr into a bounded
tail kept for failure diagnostics) so a chatty child can never wedge the pipes. On Windows,
`Process` creates those pipes synchronous before .NET 11 (dotnet/runtime#81896), so a pending read
blocks the thread it runs on; `Process`'s own event readers would run those reads on thread-pool
threads, two per standalone server for its whole life, and a host with several servers starves
its pool. The launcher therefore reads each stream on a dedicated background thread on Windows
(`ChildOutputReader`) and uses `Process`'s event readers elsewhere, where the reads are
asynchronous and hold no thread. Every reader ends before the process is released: disposal and a
failed start wait for end-of-stream inside the drain bound, then cancel any read still blocked —
a descendant can keep a write end open — with `CancelSynchronousIo`, so no reader outlives its
owner. Each Windows reader thread closes its pipe's read handle as it ends, because `Process`
never closes a redirected stream that was read synchronously. .NET 11's `Process` opens the
parent's read ends overlapped (dotnet/runtime#125643); a future .NET 11 target can read
asynchronously on that runtime without a dedicated thread. The contender spawn creates its own
overlapped stderr pipe instead (ADR-0027).

`Command[0]` is resolved once per start, before the process is created, the way a shell resolves
it, and the resolved path is what the process starts and what a failure names. A command carrying
a directory separator or a rooted path is used as written; a bare name is searched through the
PATH entries in order, skipping empty entries and resolving relative entries against the current
directory. On Windows a bare name with no extension is tried with each PATHEXT extension in
PATHEXT order — falling back to the conventional `.COM;.EXE;.BAT;.CMD` when PATHEXT is absent —
and a name already carrying an extension is tried as written; on Unix the name itself is probed
for existence and the operating system still decides executability at spawn. `CreateProcess` with
`UseShellExecute=false` appends only `.exe` and never consults PATHEXT, while an npm install on
Windows writes shim files (`opencode`, `opencode.cmd`, `opencode.ps1`) and keeps the binary
inside `node_modules`, so the shell-style search is what lets a bare name start there. A bare name
that matches nothing fails before anything is spawned, naming the command, the number of
directories searched, and the extensions tried.

A resolved Windows batch target (`.cmd`/`.bat`) is launched explicitly through the system
`cmd.exe` — located the way the downlevel tree kill locates taskkill — with `/d /s /c` and a
command line whose script path and every argument are double-quoted inside one outer quote pair,
which is the single documented `/s` parse. Determinism is the reason: `CreateProcess` will run a
batch file implicitly, but through a rule nothing in the SDK controls. Because `cmd.exe` re-parses
that line, any caller-supplied leading argument containing `&`, `|`, `<`, `>`, `^`, `%`, `!`, `"`,
CR, or LF is refused with `OpenCodeServerException` naming the offending argument, never escaped —
the fail-closed stance Rust and Node took for BatBadBut (CVE-2024-24576). The SDK's own
`--stdio --port 0` carry no such character; the resolved script path itself is not screened,
because `&`, `^`, and `%` are legal in Windows paths and a false refusal would break legitimate
installs. The batch line is composed as one string on every target, because `cmd.exe` does not
follow the MSVCRT rules `ArgumentList` applies; the non-batch case is unchanged (`ArgumentList` on
modern targets, `ProcessArgumentComposer` downlevel). The stdin ownership lease and the stdout
readiness line pass through the interpreter to the child unchanged, and `ProcessId` reports the
launcher-owned root — for a batch shim, the `cmd.exe` host rather than the server process. The
forced tree kill reaches the grandchild whenever it runs; when the server and its `cmd.exe` host
exit inside the disposal grace, no tree kill runs (see the disposal ladder below).

An optional caller-created `OpenCodeServerOutput` collector, supplied through the start options,
retains a bounded tail of both streams, including the first stdout line, for pull snapshots. It
invokes no caller code on the process readers and survives a failed start. Each snapshot reports
whether either stream was truncated; the launcher's startup exception tail remains independent.
Output finalization is best effort under the existing bounded diagnostic drain and never extends
process ownership.

Disposal is a ladder, bounded at every step so it never hangs the caller: stdin EOF (the lease
release) first, then the configured grace (`GracefulShutdownTimeout`, default 3 seconds — the
reference client's own force-kill window), then a forced whole-tree kill
(`Process.Kill(entireProcessTree: true)` on modern TFMs, `taskkill /pid … /T /F` on downlevel
Windows, and a plain `Kill()` of the root alone on downlevel non-Windows), then a final bounded
forced-exit wait. Both waits observe the owned process's own exit (the `Process.Exited`
notification), not end-of-stream on its redirected output. On .NET 8 and later,
`WaitForExitAsync` also waits for end-of-stream on a redirected stream that `Process`'s own event
readers read — the non-Windows pump reads that way; the Windows reader threads do not — and a
descendant holding stdout postpones that end-of-stream for as long as it lives. Waiting on the
exit itself releases a server that exits promptly on stdin EOF at once, on every platform, even
while a descendant keeps the pipe open. The output drain keeps its own bound. A tree kill that does
not complete is a result the ladder continues from, never an exception out of disposal: the
runtime's `AggregateException` when a process of the tree refuses the kill, a non-zero taskkill
exit (128 when the root has already exited, so its descendants were not reached), or a taskkill
still running at its 10-second bound, which is then ended so it does not outlive disposal. Every
release step after the kill runs either way.

Stdin EOF ends the server and nothing else: it releases no descendant, and a descendant that does
not watch its own stdin keeps running. The tree kill runs only when the grace expires, and on a
failed start; when the server exits inside the grace — the normal close — disposal ends there
and does not touch its descendants. Ownership is structural: the returned `OpenCodeServer` is the
only owner of its child, disposal ends exactly that child, and the operating system closes the
lease even when the owner crashes before disposal runs — coexistence with any other running server
is safe by construction, since a started door never discovers or attaches to one.

The forced-exit wait observes the directly owned process. A whole-tree kill is asynchronous: the
direct process exiting does not guarantee that every descendant has finished exiting at that
instant. Launcher acceptance separately proves bounded descendant termination after a failed
start, before any test fallback cleanup.

`CreateClient(Action<OpenCodeClientOptions>?)` pins the connection identity fail-closed: the
delegate receives a fresh identity-unset options instance, and setting `Endpoint`, `Username`, or
`Password` there is refused with `InvalidOperationException`. On success the door never mutates the
delegate's instance: it builds a distinct, freshly constructed `OpenCodeClientOptions` carrying its
own endpoint and lease credential, copying over only the behavior members (`Location`) the delegate
set; the object the delegate received stays identity-unset for as long as the caller keeps a
reference to it. Every call builds a new client over its own owned transport, which the caller
disposes.

The failure plane of every local-server door, standalone start and background-service discovery
alike, is `OpenCodeServerException : OpenCodeException`. A bounded stderr tail rides every startup
failure that reaches a running child: an exit before readiness (naming the exit
code), a readiness timeout (naming the configured bound), and a non-contract first stdout line
(quoting it) all carry it. The three pre-spawn failures carry none, because nothing ran: an
unresolvable command (naming the command, the directories searched, and the extensions tried), a
refused batch argument (naming the argument and the shim), and a spawn failure (wrapping the
underlying `Win32Exception`, naming the caller's spelling and, when they differ, the resolved
path). Caller cancellation during the readiness wait stays `OperationCanceledException` rather
than being folded into the exception type, after the child is torn down.

Launcher acceptance is real-process and three-OS. Platform-specific behavior is tested on the
platform it represents; a successful compile is not a lifecycle proof.

## Connection modes

The SDK targets all three upstream connection modes, named after upstream's own verbs — no
invented method names, and every variation rides options arguments rather than a new door.

**Standalone start** (`Standalone.start` parity, upstream `packages/cli/src/services/standalone.ts`;
CLI `--standalone`) → `OpenCodeServer.StartAsync`, above: always a fresh private server on port
zero with its own generated lease credential, never discovering or attaching to another server, so
coexistence with any running server is safe by construction. The returned working object is the
only owner of its child. Process isolation is not state isolation: the child resolves the same
user data, state, cache, and config roots as any other opencode process on the machine unless the
caller redirects `XDG_DATA_HOME`, `XDG_STATE_HOME`, `XDG_CACHE_HOME`, and `XDG_CONFIG_HOME`
through `Environment` — observed on Windows as well as on Unix. This door has landed.

**Explicit endpoint** (CLI `--server` parity; upstream builds a plain client plus a 5-second-bounded
status check and a version warning, `server-connection.ts:24-39`) → plain `OpenCodeClient`
construction. There is no dedicated SDK verb or member for this door; the validation recipe
composes two existing pieces: construct the client against the known endpoint, call
`Server.GetInfoAsync` under a caller-owned `CancellationTokenSource(TimeSpan.FromSeconds(5))`, and
compare the returned `ServerInfo.Version` against the caller's own expectation. The SDK carries no
version comparand of its own — the accepted snapshot (`spec/SNAPSHOT.md`) is a protocol identity,
not a runtime version — and the network-timeout option a first-class helper would want does not exist
yet, so **no new public member lands for this door in this arc**: a dedicated helper would
need a version comparand the SDK does not have and would pre-empt a timeout channel that does not
exist yet, and the generated client cannot gain members in this arc's territory. The recipe is
documented here and demonstrated by the sandbox's `StandaloneServerWalkthrough`, which runs the same
tail without `StartAsync` once a caller already holds an endpoint. *Noted for later:* revisit once
the network-timeout option lands rather than a caller-owned
`CancellationTokenSource` — a bounded-probe helper only earns public surface at that point.

**Background service** (`Service.discover/ensure/stop`, public export `@opencode/client/service`)
→ the registration-file mode. The first-party CLI's `opencode serve --service` publishes `url`,
`pid`, `password`, `version`, and `id` in a registration file under the XDG state root, and
`OpenCodeServer.DiscoverAsync` reads that file, checks the daemon's authenticated status, and
returns a non-owning handle: `OwnsProcess` is false and disposal is a no-op (ADR-0024). The
contract is not in the OpenAPI document; the SDK ports the accepted-pin first-party chain and
pins every file it reads in `spec/source-watch.json` (ADR-0025). The null channel reads the shared
release registration `service.json` with no legacy migration; a named channel follows the CLI's
filename, sanitization, and legacy-migration rules; a direct registration path bypasses all three.
The channel's service config is read the CLI's way: a document whose members do not decode —
`disabled` must be a boolean when present — is no config at all, so its `env` overlay and the
legacy migration it gates do not apply. Its `disabled` key is read only by CLI commands: the
connection-mode selector (`resolve` starts a standalone server instead of reaching the service when
it is true) and the pairing command's guard (`opencode pair` needs the service); the pinned
client's `Service.discover`, `ensure`, and `stop` do not read it, and neither do
`DiscoverAsync`, `EnsureAsync`, and `StopAsync` — a caller that honors a disabled service chooses
`StartAsync`, as `resolve` does.
The registration and service-config files are read the way libuv opens files, sharing read, write,
and delete, so a poll never makes the daemon's remove-on-exit fail on Windows, and a UTF-8
byte-order mark is skipped as the CLI's decoder skips it. A daemon bound to every interface
registers the unspecified address (`0.0.0.0` or `::`), which Bun connects to as loopback and .NET
refuses as a target: the connect target becomes the same family's loopback, while the registered
URL is kept verbatim for identity.
The info probe is a raw authenticated `GET /api/info` through an owned non-redirecting handler with a
two-second bound: it rides neither the pipeline's decoration policy nor its progress window, and
`ServiceInfoProbe` owns its transport and path, while `ServiceProbeResponseClassifier` decodes
only the required first-party pid/version evidence. That transport (`LoopbackTransport`) carries
two loopback rules the pipeline's transport does not: a loopback endpoint is never routed through
a proxy, so an environment proxy without `NO_PROXY` cannot hide a live daemon, and on Windows a
loopback connect disables SYN retransmission (`SIO_TCP_INITIAL_RTO`, the option libuv, Go, and the
pinned client's own runtime set for loopback), so a refused port is reported at once instead of
after the retransmissions that would outlast the bound. A failed exchange counts as a timeout
exactly when the bound expired, whatever the failure's type (the pinned client's
`timedOut: signal.aborted`). A dead daemon therefore classifies as no service, never as a timeout,
on every host; on `net472` and `netstandard2.0`, where the platform
handler has no connect seam, the probe learns the refusal over a raw pre-connect with the same
option before it sends the request. This private discovery decoder is independent
of the generated public `ServerInfo` model, whose required `Urls` does not constrain discovery. Discovery returns null for a missing, unusable, or not-ready
registration and throws only for refused input (`ArgumentException`), caller cancellation, and an
unresolvable user home (`OpenCodeServerException`).

`OpenCodeServer.StopAsync` is the CLI's `service stop`. It resolves the registration the way
discovery does (channel rules, legacy migration, or a direct file) and reads it once; asks a ready
and compatible daemon to shut its persistent terminals down through the generated
`PersistentPtys.ShutdownAsync` door, ignoring a refused or failed answer the way the CLI does;
removes the handoff sidecar the CLI keeps beside the registration (`<file>.pty-handoff`); then
ends the registered process the way the pinned client's `terminate` does — `SIGTERM`, the pin's
own poll for the process to leave (every 50 ms, up to 100 times), `SIGKILL` when it is still
there, the same poll again. On Windows both rungs are a hard kill, which is what the pinned
client's runtime does with a `SIGTERM` there. The registration is re-read and its `id`, `version`,
`url`, and `pid` compared before every signal and before the removal, so a record another service
replaced is never acted on; and the process is identified as (pid, start time), compared
immediately before every send and at every look of the poll, so a pid the operating system reuses
during the stop is never signalled and reads as the registered process being gone (ADR-0026); a
reuse before the stop runs is the Known Gap the roadmap records. A zombie — exited,
not yet reaped by its parent — reads as gone too. A missing or corrupt registration completes successfully; a sidecar
that cannot be removed and a process still running after the kill rung throw
`OpenCodeServerException`, the registration then left in place; cancellation before a signal
prevents it and after one ends the wait. Stop targets exactly one pid, never a tree, is never
implied by disposal or host shutdown, and may end a service other clients share.

`OpenCodeServer.EnsureAsync` is the CLI's managed-service election (`Service.ensure`). Its options
are validated and copied before any I/O; `Replace` and `Error` need `ExpectedVersion`. It reuses a
ready compatible daemon, replaces a version-mismatched one according to
`OpenCodeServerEnsureOptions.VersionPolicy` (Ignore strips the expected version, Replace keeps it
in the loop, Error discovers twice and throws on a ready mismatch without entering the loop), and
otherwise spawns at most two detached contenders (`opencode serve --service` by default) until a
service registers or the 120-second wall-clock bound expires. A registration without a password
never wins. Three consecutive probe timeouts on one registered daemon end it through the stop
ladder — like Stop, Ensure may end a service other clients share — after which a contender starts
at once. The replacement stop acts on the registration it probed, so a record that changed since is
never touched; its failures do not end the election, and the last one becomes the timeout's inner
exception. The spawned command registers where its own compiled channel and environment say, so
`Channel`, `Command`, and `Environment` must agree with the registration the call reads, or the call
waits out the bound. Contenders are reaped by the SDK as they exit. `OnStart` fires at most once.
The returned handle is the same non-owning shape discovery returns. Persistent-terminal sidecar I/O is behind
`IServicePtyHandoff`: `prepare` requests the handoff ticket under the request bound through the
SDK's own request pipeline and keeps it raw, so it reaches the replacement exactly as the route
answered it — the receiving daemon decodes it with its own schema — and publishes it beside the
registration through an owner-only temporary file and a replace-on-success rename (`File.Replace`
on the downlevel targets). It reuses a fresh matching sidecar and, whenever the request fails,
first re-checks for one a concurrent caller published; when the daemon answers 404 it shuts its
terminals down — a shutdown failure other than 404 fails the preparation, as upstream's does — and
publishes a null sidecar with a 30-second expiry. A failure keeps its cause as the inner exception;
`environment` adopts an unexpired sidecar whose source still matches the current registration (or
whose registration is gone) as `OPENCODE_PTY_HANDOFF` and removes the variable otherwise;
`complete` clears only a sidecar a different source wrote, with no expiry check; and `clear`
removes it idempotently, the same seam Stop's sidecar clear routes through.

The daemon side of the lifecycle is the spawned CLI's, not the SDK's: the incumbent check, the
exit-0 loser on a port another daemon holds, the `chdir` into the user home, and the registration
write all run inside `opencode serve --service` (`server-process.ts`), and the election reads a
loser's exit 0 the way upstream's does. The CLI's `reconnect` and `restart` have no door of their
own; a caller composes them from `StopAsync` and `EnsureAsync`.

Where the SDK deliberately differs from the pinned chain, it differs here, each for a recorded
reason (ADR-0031):

- **Stricter readers than upstream's `JSON.parse`.** The registration and service-config readers
  refuse a repeated member. The registration reader also refuses a pid that is not a positive
  32-bit integer and a URL that is not absolute HTTP or HTTPS. The sidecar reader refuses a pid
  that is not integral. A refused document reads as absent. Reason: the repository's fail-closed
  boundary, which treats malformed state as missing rather than acting on it.
- **No registration without a password.** Discover and Ensure never hand out such a registration,
  where upstream would connect without credentials. Reason: the SDK never yields an
  unauthenticated connection. The pinned daemon always writes a password, so the two only differ
  for a hand-made file.
- **UTF-8 Basic credential in the probe.** Upstream's probe uses Latin-1 `btoa`, which throws on
  any password outside Latin-1, while its sidecar writer, its promise client, and the server all
  use UTF-8. Reason: the SDK agrees with the rest of upstream, not with the one encoder that fails.
- **No proxy for a loopback probe.** Reason: an environment proxy without `NO_PROXY` would
  otherwise hide a live daemon.

Everything else follows the chain at the pin.
