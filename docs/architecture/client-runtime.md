# Client Runtime Architecture

Date: 2026-10-26

Canonical current rules for client construction, transport ownership, API errors, streams, and the
local server launcher. Protocol and generated-model rules live in
`protocol-and-generation.md`.

Every behaviour in this document that the SDK implements by hand follows upstream's first-party
behaviour at the pin as a north star, with the Bun build as the reference runtime; a fix takes the
shape upstream's behaviour has. A divergence is the maintainer's decision, never an implementer's:
work that finds upstream's behaviour wrong or unworkable stops and brings the evidence to the
maintainer (`docs/engineering/deviation-protocol.md`). An approved divergence is either the
idiomatic .NET form of the same contract, measurably safer or more robust, or a superset of
upstream's behaviour. Each one is listed, with its reason, in the section it concerns (ADR-0031).

## Construction and transport ownership

- `OpenCodeClient(OpenCodeClientOptions)` is the only public construction path (ADR-0010).
- The SDK owns a singleton-friendly transport and disables automatic redirects on every owned
  handler. A surfaced 3xx is an undeclared protocol response, never a route, authority, method, or
  credential transition the SDK follows implicitly. Modern targets use `SocketsHttpHandler` with a
  120-second pooled connection lifetime and a 3-second pooled idle timeout. Downlevel targets
  configure the endpoint-and-proxy `ServicePoint` with an effectively unbounded connection limit,
  the same 120-second connection lease and the same 3-second `MaxIdleTime` before construction and
  each owned send, avoiding both long-lived stream starvation and stale endpoint retention without
  mutating process-global defaults. The endpoint-scoped `ServicePoint` itself is process-shared, so
  this policy also governs other process traffic to the same endpoint-and-proxy pair (ADR-0010).
- The idle timeout keeps the transport from reusing a socket the server has already closed. The
  pinned server sends `Keep-Alive: timeout=5` and closes an idle connection about six seconds after
  its last response. Upstream's two first-party clients avoid that close by different mechanisms:
  - undici, the `fetch` under Node, stops reusing a connection at the server's hint minus a
    2-second threshold, which is 3 s here;
  - Bun's `fetch` ignores the hint and instead evicts a pooled socket as soon as the server's FIN
    arrives.

  The SDK sets undici's value for this server's hint, 3 seconds, as a fixed idle timeout on every
  target. A live test pins the hint, so a pin refresh that changes it names this value for
  re-derivation.
  - **.NET Framework is where the timer matters.** It ignores the hint, so without `MaxIdleTime`
    the `ServicePoint` kept an idle connection for its 100-second default, well past the server's
    close.
  - **Modern .NET** already avoids the close on its own: `SocketsHttpHandler` stops reusing a
    connection at the hint minus one second, 4 s here, and before it reuses a pooled connection it
    checks whether the server has already closed it. The explicit 3 seconds align it with undici.
  - The `netstandard2.0` asset running on a modern .NET runtime can't take the explicit setting,
    because its `HttpClientHandler` exposes no idle timeout and ignores `ServicePoint`. It keeps
    that built-in hint-minus-one-second limit (4 s here). No CI leg runs that combination.
- The `(HttpClient, options)` constructor is internal friend-assembly surface for this repository's
  tests and benchmarks. There is no public transport-injection constructor (ADR-0010).
- `OpenCode.Sdk.Extensions` registers one singleton root client and every sub-client, each only when
  the container does not already hold that service type, so a consumer's earlier registration wins.
  Default sub-clients resolve from the registered root. It does not use `IHttpClientFactory` or
  depend on `Microsoft.Extensions.Http` (ADR-0010).
- No consumer transport-composition seam is public today. Adding one requires a concrete consumer
  need and a deliberate design; omission is reversible because a future public seam is additive
  (ADR-0010).
- Retry, telemetry and hooks are not part of `1.0.0`; each can land later as an addition through
  the policy roster (ADR-0018) under two rules. A new capability is off by default, so an existing
  call behaves as before until the consumer opts in; retry in particular never starts retrying a
  call that did not ask for it. `OpenCodeClient(OpenCodeClientOptions)` stays the only public
  construction path, with today's transport and server ownership, so a capability arrives as a new
  option or a new type and never as a changed constructor.
- Client options are snapshotted at construction. Explicit-endpoint construction reads no process
  environment variable; consumers resolve their own configuration. `OpenCodeServer.DiscoverAsync`
  and `OpenCodeServer.StopAsync` are the doors that read the environment, and they read exactly
  `XDG_STATE_HOME`, `XDG_CONFIG_HOME`, `OPENCODE_CONFIG_DIR`, and the user profile (`USERPROFILE`
  on Windows, `HOME` elsewhere), because the registration file those roots locate is that mode's
  complete endpoint-and-credential contract; they read no `OPENCODE_*` credential variable.

`Pipeline` owns construction, the policy roster, the one-shot plane and disposal orchestration.
`ClientLifetime` owns root admission state and the saved lifetime token; `PipelineExchange` creates
messages and dispatches them through that roster. `StreamPlane` owns a single stream's opening,
status and media walls, framing and response lifetime. `ReconnectingEventStream` owns the reconnect
loop and its live caller/disposed ordering, using `StreamAttempt` for each attempt's windows.
`OperationTraits` supplies per-operation rules once through `OperationRequest.Describe`; the exchange
resolves the message's network window from those carried traits.

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
- **Failed upgrade.** A missing PTY, or a `Location` naming a directory that does not exist, answers
  plain HTTP 404 before upgrading; a rejected credential or origin answers 401/403. A failed upgrade
  has no response spine, so it cannot ride ADR-0007's envelope machinery: the transport plane is the
  honest channel and every case throws `OpenCodeTransportException` naming the PTY and the cause.
  Modern targets read the status from `ClientWebSocket.HttpStatusCode` (enabled by
  `CollectHttpResponseDetails`); `net472` and `netstandard2.0` cannot report it, so the failure
  names the connect context instead of guessing a status. `platform-and-packaging.md` owns the
  target-framework detail.
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
  undelivered frames, and can grow when consumers are absent or slow. It is unbounded, as
  upstream's live path is: an unbounded per-connection outbox, with only its 2 MiB replay ring
  bounded. A queued frame of either family retains about 1 byte per byte of output: a `PtySession`
  frame keeps the received UTF-8 and decodes `Text` on its first read. So an absent consumer of a
  verbose build (about 250,000 mostly ASCII characters a second) grows by about 15 MB a minute.
  Connection termination delivers the valid queued prefix before normal completion or the recorded
  transport/protocol error. Later readers observe the same terminal outcome; consumer cancellation
  does not erase it.
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
  adapter at connect — before a core exists at all. The pipeline publishes the client's
  `TimeProvider` in `ConnectionSnapshot`; both connect paths pass it through their sessions to
  the core's send deadlines and send queue. Send admission and close-output each arm their own
  independent graceful-close budget on that same clock.

## Error channels

- API failures throw through `OpenCodeException` -> `OpenCodeApiException` by default. Tagged
  protocol error payloads remain generated, typed data on the exception (ADR-0007).
- A one-shot call may select per-call `NoThrow`, returning the same typed error data on its response
  spine. There is no client-level error-behavior switch (ADR-0007).
- Throwing API errors retain the raw response body. `NoThrow` responses retain it on the shared
  response spine, including when typed error parsing fails (ADR-0007). A response's printed form
  (`ToString`) leaves the raw body out, because a server can echo request data into it; the status,
  the error flag, and the typed error still print.
- Transport, status/framing, unrecovered JSON or materialization, dispatch, and cancellation-wrapping
  failures throw `OpenCodeTransportException`; stream payload tolerance is scoped below. `NoThrow`
  applies only to declared API errors and never suppresses transport failures. Undeclared 3xx
  responses are protocol failures on both one-shot and streaming paths (ADR-0007, ADR-0014).
- Caller cancellation of a public asynchronous operation or enumeration throws
  `OperationCanceledException` carrying the caller's token, not an SDK-owned linked token. The rule
  applies to HTTP operations, SSE, cursor traversal, both terminal families, and `StartAsync`,
  `DiscoverAsync`, `EnsureAsync`, and `StopAsync`. This identity guarantee applies when one caller
  token is supplied, including the same token supplied at both enumeration entry points; two distinct
  cancellable entry tokens are both observed but have no token-identity guarantee.
- Disposing the root client interrupts its in-flight HTTP operations and SSE streams and reports
  `ObjectDisposedException`; caller cancellation already observed for the same failure takes
  precedence. A terminal session owns its connection separately: its session-disposal rules apply.
- Streaming operations take `OpenCodeRequestOptions` and return a stream rather than a response
  envelope. Per-call `Location` applies to every connection attempt; `NoThrow` and an undefined
  `ErrorBehavior` are refused at the call, before enumeration. Their failures always throw (ADR-0007).
- Network progress is bounded by a per-read window: the send and every buffered body read must
  progress inside it, and each read that progresses re-arms it, so a slow-but-flowing body
  survives while a stalled one fails. Its length is the call's `OpenCodeRequestOptions.NetworkTimeout`
  when set; otherwise an ordinary operation takes `OpenCodeClientOptions.NetworkTimeout`, or 100 s,
  the .NET convention. The operations that answer only when work ends —
  `experimental.session.wait`, `session.shell`, `session.command`, `session.generate`,
  `experimental.generate.text`, `experimental.generate.image`,
  `experimental.generate.decisions` — run with no window
  unless the call sets one, so the caller's token is their deadline, as upstream's client bounds
  them; a client-wide value does not reach them. A hand-written table keyed by operation id names
  them; a Tools test holds its ids to the pinned document, and the source watch pins the upstream
  lines that block. Both members accept a value above zero and at most `int.MaxValue` ms, or
  `Timeout.InfiniteTimeSpan`, refused when set. A raw-byte request body is the caller's
  `Stream`: it is read once from its current position to its end, never disposed, rewound, or
  buffered whole, declares a `Content-Length` only when the stream can seek, refuses a second
  send, and restarts the window with each chunk written, so the upload is bounded by progress as
  well. The pipeline owns this timer — the
  owned transport's `HttpClient.Timeout` is infinite so two mechanisms cannot race, and a
  caller-supplied client's own timeout bounds only its send. A stalled read no token can reach is
  interrupted by disposing the content. An error body returned while opening a stream buffers
  under the same window; a successful SSE body remains live until caller cancellation, server
  completion, or failure, except `event.subscribe`, which runs under its reconnect windows
  (Server-sent events, below). Caller cancellation uses the public cancellation contract above;
  an exhausted progress window maps to `OpenCodeTransportException`.
  On streams other than `event.subscribe`, a per-call `NetworkTimeout` overrides the client value
  only while opening, including buffering an opening error body; it does not bound reads of the
  successful live body. `event.subscribe` refuses any non-null per-call `NetworkTimeout`, including
  `Timeout.InfiniteTimeSpan`, because its connection and idle windows govern it instead. Automatic
  cursor traversals apply the options to every page fetch, so each page's send and buffered reads
  receive their own progress window, not one deadline for the whole walk.
  Successful JSON bodies materialize directly from validated UTF-8 bytes when possible. One decoding
  policy applies the modern `HttpContent` charset/BOM and malformed-UTF-8 replacement algorithm on
  every target framework and on both success and error planes. Error bodies stay decoded strings so
  throwing and `NoThrow` paths retain exact `RawBody`. Declared no-content successes drain an
  unexpected body with the buffered response and ignore it (ADR-0007, ADR-0014).
- A schema-valid reserved failure frame throws `OpenCodeStreamFailureException`, a subtype of
  `OpenCodeTransportException`, with typed causes on its non-null `Causes` collection. Invalid cause
  JSON, null materialization, and declared-but-impossible cause tags remain base transport/protocol
  failures rather than partially typed exceptions (ADR-0015).

## Server-sent events

- Streams are exposed as `IAsyncEnumerable<T>` and open lazily on first enumeration.
- Cancellation closes the stream through the enumeration token. On every target, caller cancellation
  or root-client disposal releases the live response even while the consumer holds a yielded event.
  Response disposal also interrupts downlevel reads that do not observe an async read token;
  disposal-induced failures use the cancellation and disposal rules in Error channels.
- `event.subscribe` reconnects as upstream's first-party client does (`client/src/solid/connection.ts`
  at the pin), and no other stream reconnects:
  - Each attempt has 2 s to answer and deliver `server.connected` as its first event, or it fails.
    The first open runs the same check. If the connect bound passes before response headers arrive,
    the failure says "The opencode server did not answer the event stream request within 2 seconds."
    If headers arrived but no first event followed, it says "The opencode server answered the event
    stream request but sent no event within 2 seconds."
  - A connected stream that receives no byte for its idle window is cut: 45 s, or
    `OpenCodeClientOptions.EventStreamIdleTimeout`, as upstream's `idleTimeout` option sets it. The
    option accepts any window above zero and at most `int.MaxValue` milliseconds, but no window
    that never ends, since upstream has none; it is read once, when the client is built.
    Any byte re-arms the window, including the server's keepalive comment every 15 s. The window
    stops while the consumer holds an event, because a pull-based consumer's own time is not the
    server's silence.
  - Every failure is followed by a new attempt 1 s later, with no backoff. That covers a refused
    connection, any HTTP error status (a 401 or a 404 included), an undeclared success, a body that
    is not an event stream, malformed framing or JSON, a payload neither its primary nor its
    unknown-arm metadata can materialize, a failure frame, the server ending the stream, and either
    window passing. Tolerated payload skew is not a failed attempt.
  - No replay is requested. Each connection yields its own `server.connected`, so a second one
    tells the consumer that events published in between are gone and authoritative state should be
    refreshed.
  - A stream that has failed every attempt for 1 minute gives up and throws the last failure. The
    minute runs from the drop, or from the first attempt for a first open, and it stops once an
    attempt delivers `server.connected`. When it passes, it cuts whatever is in flight. A window
    passing surfaces as `OpenCodeTransportException`, never as a cancellation.
  - Caller cancellation or root-client disposal releases the live connection at once, including while
    the consumer holds an event. An opening, reading, or reconnecting wait ends with the cancellation
    or disposal exception specified in Error channels; a suspended enumeration reports it on its next
    pull. Nothing else ends it except the give-up window.
  - Recorded divergences, both intended:
    - Upstream retries forever and shows a connection status; the SDK has no status to show, so an
      endless silent retry would hang `await foreach`. The consequence for consumers is that a wrong
      address or a refused credential surfaces after one minute, not at once, as the error the server
      last answered. It reverses if the SDK gains a connection-status surface a consumer can watch,
      which would let it retry forever as upstream does.
    - Upstream's clients read the bus on a background loop into a 4,096-event queue per subscriber
      and fail a subscriber that falls behind (`client/src/shared-events.ts` at the pin). The SDK
      reads only when the consumer pulls, as .NET's `IAsyncEnumerable` model does, so back-pressure
      and pacing stay with the consumer, and it runs no background reader, no per-subscriber queue
      and no drop policy of its own. The consequences: a long-held event delays noticing a lost
      server, and a backlog sits in TCP and in the server's own 4,096-event limit. The streaming
      guide's live-bus section shows the short loop that hands events off. It reverses if upstream's
      first-party bus stops reading ahead of its consumer, or if a consumer need appears that pulling
      can't meet, such as noticing a lost server while an event is held.
  - `OpenCodeClientOptions.NetworkTimeout` does not reach `event.subscribe`, and a non-null per-call
    `NetworkTimeout` is refused, including `Timeout.InfiniteTimeSpan`. The 2 s connect bound and the
    1 s delay are fixed, as in upstream; per-call `Location` is sent on every reconnect attempt.
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
  impossible is a protocol failure, not an unknown variant (ADR-0015). A stream payload union that
  reaches one is refused at generation, so the tolerance below never reaches it (ADR-0038).
- On `event.subscribe` and `session.log`, a payload whose normal materialization throws
  `JsonException` arrives as its union's existing unknown arm when that arm's converter can hold
  the same frame; the connection or request continues (ADR-0038). Invalid JSON and refused carriers
  keep the malformed-payload transport failure with the original decode exception as inner. A null
  primary result keeps its distinct null-payload failure. Only `JsonException` is caught; other
  converter exceptions propagate. Both streams have this scoped tolerance because the pinned
  first-party promise SSE reader parses JSON and casts without schema decoding, not because every
  valid JSON value is representable by the SDK.
- A body cut in the middle of an event is reported as a transport failure rather than dispatched
  as malformed payload data.

## Pagination

- A supported cursor-list operation has two generated doors: `List*Async` returns one endpoint-
  specific page envelope, while `Enumerate*Async` returns a `CursorSequence<TPage, TItem>` that
  lazily yields the items across pages and, through its `Pages` property, the same traversal's page
  envelopes. Explicit pages retain per-call `NoThrow`; automatic traversal takes
  `OpenCodeRequestOptions`, applies location and network timeout to every page fetch, and refuses
  `NoThrow` at the call before enumeration. API errors always throw when their page is reached
  (ADR-0007, ADR-0017).
- `CursorSequence<TPage, TItem>` is a recipe, not a buffer: the item door and the page door each
  drive the one traversal core independently, so enumerating either performs its own requests, and
  enumerating both performs both walks. Its `TPage` is the generated response type — no page
  vocabulary and no page-size contract is introduced. The token given to `Enumerate*Async` and the
  token given to `GetAsyncEnumerator`/`WithCancellation` are both observed.
- `ListRequest` carries the `limit`, first-page `order`, and opaque `cursor` channels, and a derived
  request may declare its own filters beside them. `limit` is an `int?`, by curation: the pin
  declares it a string, but the server decodes an integer and upstream's client takes a number.
  `ListCursor` preserves the response's optional `previous` and `next` values. The first automatic request is sent unchanged,
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
(upstream `Standalone.start` parity), hand-written with no process-management dependency
(ADR-0001): over the SDK's own spawn, job, exit-status, and tree-kill seams on Windows, and over its
own spawn, group signal, and exit-status seams on Linux and macOS (ADR-0032). The readiness wait, the
failed-start and disposal flow, and the order of the release are one flow both platforms share; the
platforms differ in how the server is created and placed, how its exit is observed, and the rungs of
its ladder. Every call is always a fresh private
server on port zero: the caller's `Command` — `opencode serve` by default, the command the
`@opencode/cli` package installs (the package also installs a transitional `opencode2` alias
pointing at the same executable) — plus `--stdio --port 0` is the argv, and a freshly generated
lease credential is injected into the child environment as `OPENCODE_PASSWORD`, after any
caller-supplied `Environment` entries so it can never be shadowed. Readiness is the single JSON
stdout line the child prints once fully booted; stdin stays open as the ownership lease for as long
as the server runs, and every later stdout line plus all of stderr is drained continuously (stderr
into a bounded tail kept for failure diagnostics) so a chatty child can never wedge the pipes. The
launcher creates the pipes itself on every OS, and their reads are asynchronous and hold no thread:
on Linux and macOS a pipe read is socket-backed, and on Windows the parent's ends are overlapped
named pipes whose reads complete on the I/O completion port, on every target, .NET Framework
included. A host with several servers therefore holds no thread per server, where `Process`'s
synchronous pipes before .NET 11 (dotnet/runtime#81896) would hold two pool threads per server for
its whole life. Every reader ends before the child's handles are released: disposal with a
collector and a failed start drain for at most one second, and the release then cancels the pending
reads — a descendant can keep a write end open — and closes the pipes; on .NET Framework, where no
token reaches a pipe read, closing the pipe is what ends it. The background-service contender spawns
through the same Windows spawn, with the same pipes (ADR-0027).

`Command[0]` is resolved once per start, before the process is created, the way a shell resolves
it, and the resolved path is what the process starts and what a failure names. A command carrying
a directory separator or a rooted path is used as written, except that on Linux and macOS a
relative path is made absolute against the caller's current directory, because the child may start
in another `WorkingDirectory`; a bare name is searched through the PATH entries in order, skipping
empty entries and resolving relative entries against the current directory. On Windows a bare
name with no extension is tried with each PATHEXT extension in PATHEXT order — falling back to the
conventional `.COM;.EXE;.BAT;.CMD` when PATHEXT is absent — and a name already carrying an
extension is tried as written; on Unix the name itself is probed for existence and the operating
system still decides executability at spawn. `CreateProcess` with `UseShellExecute=false` appends
only `.exe` and never consults PATHEXT, while an npm install on Windows writes shim files
(`opencode`, `opencode.cmd`, `opencode.ps1`) and keeps the binary inside `node_modules`, so the
shell-style search is what lets a bare name start there. A bare name that matches nothing fails
before anything is spawned, naming the command, the number of directories searched, and the
extensions tried.

A resolved Windows batch target (`.cmd`/`.bat`) is launched explicitly through the system
`cmd.exe` — located the way the tree kill locates taskkill — with `/d /s /c` and a
command line whose script path and every argument are double-quoted inside one outer quote pair,
which is the single documented `/s` parse. Determinism is the reason: `CreateProcess` will run a
batch file implicitly, but through a rule nothing in the SDK controls. Because `cmd.exe` re-parses
that line, any caller-supplied leading argument containing `&`, `|`, `<`, `>`, `^`, `%`, `!`, `"`,
CR, or LF is refused with `OpenCodeServerException` naming the offending argument, never escaped —
the fail-closed stance Rust and Node took for BatBadBut (CVE-2024-24576). The SDK's own
`--stdio --port 0` carry no such character; the resolved script path itself is not screened,
because `&`, `^`, and `%` are legal in Windows paths and a false refusal would break legitimate
installs. The batch line is composed as one string, because `cmd.exe` does not follow the MSVCRT
rules; any other target's command line is its argv composed with the MSVCRT rules
(`ProcessArgumentComposer`), which the child's runtime parses back. The stdin ownership lease and
the stdout readiness line pass through the interpreter to the child unchanged, and `ProcessId`
reports the launcher-owned root — for a batch shim, the `cmd.exe` host rather than the server
process. The job holds that root: the real server, `cmd.exe`'s child, is outside it, so when the
owner dies without disposing, the server ends only through its stdin lease, as it does under
upstream's own launch of a `.cmd` file. The npm install's bare `opencode` resolves to exactly such a
shim; the full owner-crash guarantee needs `Command` to name `opencode.exe`, and a shim of another
installer is safe only if it ties its child to itself. The tree kill reaches the grandchild whenever
it runs; when `cmd.exe` has already exited, no tree kill runs (see the disposal ladder below).

On Windows the server is placed in a process-wide job that ends it with its owner, the job libuv
gives upstream's server: one job per process, created on the first start and never closed by the
SDK, with exactly libuv's four limits — kill on close, so the kernel ends every server when the
host's handle to the job closes, which it does when the host ends, however it ends; breakaway
allowed and silent breakaway, so no process a server creates joins the job by inheritance and its
detached descendants outlive it; and death on an unhandled exception, so a crashing server ends
without an error-reporting dialog. A server built on libuv (Bun, Node) places its other children in
a job of its own, which nests under the launcher's, so those children end with the owner too. The
job handle is not inheritable, and the host itself is not added. The server is created suspended
with `CreateProcessW`, assigned to the job, and only then resumed: it runs no instruction outside
the job. An assignment refused with `ERROR_ACCESS_DENIED` is tolerated, as libuv tolerates it (a job
of the host's that cannot nest), and the server then ends with its owner through the stdin lease
alone; one refused with `ERROR_INVALID_PARAMETER` is tried once more in a fresh job, which serves
every later start while the old job stays held once it takes the server, and is closed, the old job
staying in use, when it refuses too; any other refusal, a job that cannot be created, and a main
thread that cannot be resumed end the suspended server with code 1 and fail the start.
The spawn passes an explicit inherited-handle list (`PROC_THREAD_ATTRIBUTE_HANDLE_LIST`), so the
server receives its three standard handles and nothing else the host holds. Every handle the SDK
creates for a child is non-inheritable; under one lock every Windows spawn of the SDK takes (the
launcher, the background-service contender, and the tree kill), the spawn duplicates the child's
ends as inheritable, creates the child, and closes the duplicates, and the parent then closes its
own copies of the child's ends. A `Process.Start` on another thread of the host can still inherit a
duplicate inside that call, on every runtime: it passes every inheritable handle under the runtime's
own spawn lock, which the SDK's spawn does not take. That lock is private; reaching it through
reflection was considered and rejected, because it breaks with any runtime change and under
trimming.
Stdin, stdout, and stderr are local named pipes under `\\.\pipe\LOCAL\` (the one namespace an
AppContainer host can create pipes in), created with a fresh name, as the first and only instance,
refusing remote clients: the parent's end is overlapped, the child's end synchronous, each one
direction with libuv's attribute rights. Every child is created with `SW_HIDE`, and with
`CREATE_NO_WINDOW` unless a standard handle is the host's own, libuv's rule for a hidden child: a
server whose stdio the launcher pipes has a hidden console of its own. Environment names compare
ignoring case, so a caller entry spelled in another case than the host's replaces it. With
`OPENCODE_PRINT_LOGS=1` in the host's environment the server's stderr is the host's own stderr, as
upstream's is, so no stderr is collected or quoted, and the server then shares the host's console,
as upstream's does: a Ctrl+C in that console reaches the server, even when the host handles it,
which is diagnostics-mode behaviour, and a batch-shim root answers it with `cmd.exe`'s "Terminate
batch job (Y/N)?" prompt. A NUL is refused before the spawn, as the launcher's failure contract
states, and a spawn failure in a working directory names the directory. Three residuals are
recorded, not handled: the named pipes carry the default access rules, so a local process that
connects to one first makes the start fail (it can read nothing); an owner that crashes between the
server's creation and its assignment leaves that one server suspended until logoff; and on a Windows
edition without `taskkill.exe` in System32 (possibly Nano Server, not verified) every tree kill
fails, so disposal ends the root alone, through the fallback.

On Linux and macOS the server is placed the way upstream places it: `posix_spawnp` with
`POSIX_SPAWN_SETSID`, so it leads a session and a process group of its own, out of reach of a
Ctrl+C or a hangup aimed at the host's terminal, and its pid names a group the launcher can signal
whole. Every signal starts at its default disposition with an empty mask, libuv's child state,
which `posix_spawn` alone and `Process` do not give a signal the host ignores (the .NET runtime
ignores `SIGPIPE`). Standard input, output, and error are pipes wired by file actions. The pipe ends
are close-on-exec from their creation on Linux; macOS has no atomic close-on-exec pipe, so the
spawn adds `POSIX_SPAWN_CLOEXEC_DEFAULT`, as libuv does, and the child receives no descriptor its
file actions do not name. Pipe creation and spawn share one SDK-wide lock for the launcher and the
background-service contender, so two SDK spawns never overlap; a `Process.Start` on another thread
that forks inside a macOS pipe's creation can still inherit its ends, and nothing in managed code
can prevent that. A `WorkingDirectory` is applied by `posix_spawn_file_actions_addchdir_np`;
where the C library predates it (glibc before 2.29, so RHEL 8 among supported hosts), the child is
started through `/usr/bin/env -C <dir> <absolute command> <args>`, which keeps the pid across
`exec` and leaves the signal state alone, and a missing directory then fails the start as an exit
with code 125 whose stderr names the directory. A blank `WorkingDirectory` counts as none, as it
does for `Process`. A spawn failure in a working directory names the directory, because a missing
directory and a missing executable fail with the same errno. POSIX systems other than Linux and
macOS are refused before anything is spawned. Every binding has a fixed, non-variadic signature,
because a variadic function's arguments are passed differently on Apple arm64. With
`OPENCODE_PRINT_LOGS=1` in the host's environment (read from the host, as upstream reads it, not
from the child's entries), the server's stderr is the host's own stderr instead of a pipe, so no
stderr is collected or quoted.

The server's exit, on Linux and macOS, is observed by one background thread per child that blocks
in `waitid` without reaping, then reaps the child under the lock the pid fallback of the ladder
takes and decodes the wait status into an exit code or a terminating signal. Under the same lock,
the pid fallback first asks `waitpid` without hanging whether the pid is still an unreaped child:
it signals only a child that has not exited, keeps the status of one it reaps there instead, and
sends nothing to a pid that is no child any more because something else reaped it. A signal sent to
the pid therefore cannot reach a process that reused it, unless a reaper outside the launcher takes
the child between that check and the signal, as the .NET runtime does when it runs as pid 1. Apart
from the probe described next, nothing polls, and no process-wide signal handler is installed. An exception on that thread reports the exit as unknown rather than
ending the host. When the host ignores `SIGCHLD`, or its
action carries `SA_NOCLDWAIT`, the kernel reaps children itself and a blocked wait could sleep until
every child of the host is gone (macOS); the launcher reads the disposition at start and then
probes for the exit instead, at most 100 ms apart, with `kill(pid, 0)` and a `waitpid` that does
not hang. On Linux, and on macOS when the host set the ignore itself, something other than the
launcher reaps the child, and the exit is reported with an unknown status. On macOS an ignore the
host inherited across `exec` still leaves a zombie; the probe reaps it and reports its real status.
macOS also ends the blocked wait when the child only stops or continues, as .NET's
`Process.Kill(entireProcessTree: true)` stops a tree before it kills it; the reap then finds no exit,
and the watch probes the same way until the child is reaped, so a stop is never reported as an exit.
A runtime that reaps every child first (the .NET runtime does when it runs as pid 1) leaves no
status to read either, and that exit is also reported as unknown rather than invented.

On Windows the server's exit is observed through a native registered wait on a duplicate of its
process handle, as libuv observes it (`WT_EXECUTEINWAITTHREAD | WT_EXECUTEONLYONCE`), so no thread
is held per server. The callback runs on the operating system's wait thread itself, never in the
.NET thread pool's queue, so a busy pool cannot delay an exit the kernel has already reported: the
readiness verdict, the ladder's check for an exited root, and `OpenCodeServer.Exited` all read the
one exit the callback completes. The code is
`GetExitCodeProcess`'s in its full 32 bits, the bits `Process.ExitCode` gives: an access violation
is reported as `-1073741819 (0xC0000005)`. The launcher holds the process handle until the server
has exited and its release ran, whichever comes second, so the root's pid names no other process
while either rung can reach it.

An optional caller-created `OpenCodeServerOutput` collector, supplied through the start options,
retains a bounded tail of both streams, including the first stdout line, for pull snapshots. It
invokes no caller code on the process readers and survives a failed start. Each snapshot reports
whether either stream was truncated; the launcher's startup exception tail remains independent.
Output finalization is best effort under the existing bounded diagnostic drain and never extends
process ownership. Disposal keeps collecting while it ends the server, so a server writing during
its shutdown never blocks on a full pipe, then drains the output inside its bound before the
collection closes: the final snapshot holds what the server wrote until it ended. That drain is at
most one second, after the ladder.

The readiness wait ends at the first stdout line, at the caller's cancellation, at the configured
timeout, or when the child shows it never will be ready: the root's exit, observed by the exit watch
rather than by end-of-stream, or stdout reaching end-of-stream while the server keeps running, which
upstream also fails at once. The output is then drained once,
for at most one second — a descendant holding stdout open costs that second and no more — and a
readiness line already in the pipe still wins, because the first line wins whatever follows it.

At a canceled readiness wait, caller cancellation is captured before failed-start cleanup. Without
that caller cancellation, a recorded readiness line wins over a recorded root exit, then stdout
completion; only the absence of those outcomes is a readiness timeout. An early-end diagnostic
drain still gives the first readiness line priority. Cancellation arriving during cleanup does not
replace the failure already selected.

Readiness, drain, both disposal ladders, background reap, reader release and tree-kill bounds run
on `LauncherSeams.Time`, chosen once by the standalone launcher's composition root.

Disposal is a ladder, bounded at every step so it never hangs the caller. On Windows it follows
upstream's rungs and timing: the grace (`GracefulShutdownTimeout`, default 3 seconds — the
reference client's own force-kill window) starts with the first rung and bounds it.

1. A root that already exited is left alone: `taskkill` finds only live processes, so on an exited
   root it reaches none of the root's descendants and costs a run of about 300 ms.
2. At once, the tree kill: `taskkill /pid N /T /F`, upstream's own Windows group kill, which ends
   every live descendant by parent pid, detached ones included. It runs from its absolute System32
   path rather than resolved through `PATH` and `cmd.exe` as upstream runs it, so a writable `PATH`
   entry cannot substitute the executable, through the SDK's own spawn with no handle of the
   host's, and its exit is awaited without a thread. When it cannot start or exits non-zero,
   `TerminateProcess` ends the root with code 1, upstream's fallback. A kill the grace overtakes is
   ended, and the next rung follows at once.
3. Within what is left of the grace, the root's own exit, observed by the exit watch, not by
   end-of-stream on its output.
4. When the root outlived the grace, the same pair again, the tree kill bounded at 10 seconds,
   unless the root exited meanwhile; then a bounded wait of 10 seconds for the root.
5. With a collector, the output drains for at most one second, and the collection closes.
6. Stdin closes, the readers are released, the pipes close, and the exit watch is released once
   the root has exited.

Stdin is not closed first: upstream never closes it in its release, and the server tolerates being
ended by force, because upstream ends it that way on Windows. A disposal therefore takes at most the
grace plus 20 seconds, the drain, and the release; a failed start runs the same ladder. A tree kill
that does not complete is a result the ladder continues from, never an exception out of disposal.

Ownership is structural: the returned `OpenCodeServer` is the only owner of its child, disposal ends
exactly that child, and coexistence with any other running server is safe by construction, since a
started door never discovers or attaches to one. When the owner dies without disposing, the job
ends the server on Windows, however the owner died; the stdin lease, which the operating system
closes with the owner, remains the fallback for a server the job does not hold (a tolerated refusal,
a batch shim's child).

`OpenCodeServer.Exited` reports the end of an owned server, upstream's `exited`: a
`Task<OpenCodeServerExit>` that completes once the root's exit is observed and its output has
ended, or one second after the exit when a descendant still holds a pipe open, which is upstream's
`close`, or `exit` plus one second. The task starts that wait at the exit itself, so it completes
after a natural exit with no disposal, and disposal's release of the readers ends the wait at once.
It never faults and is never canceled. The end is an exit code, a terminating signal, or unknown:
`Kind` names which, and exactly its members are set. A code is the one the exit watch reads, in
full 32 bits on Windows. A signal carries its standard name for the host's numbering, such as
`SIGTERM`, as upstream reports it, and its number. A signal number with no standard name, such as
a Linux real-time signal, keeps the number with no name, and no name is made up. Unknown is an
exit whose status could not be read, such as one something else reaped. A handle from
`DiscoverAsync` or `EnsureAsync` owns no process, so its `Exited` throws
`InvalidOperationException`: upstream's attach endpoint carries no exit either.

On Linux and macOS the ladder follows upstream's rungs; where the two end in a different place, the
difference is one of the recorded divergences below.

1. A root that already exited on its own decides by its status. With a non-zero code and no
   signal, its group's survivors are ended by the rungs below, as a live root's group is. With an
   unknown status, the group is probed with `kill(-group, 0)`, and any member left is ended the
   same way. With code 0 or on a signal, the ladder stops and the group is left alone. Signalling
   the group cannot reach an unrelated process while the group still has members, because its id
   cannot be reused until then.
2. `SIGTERM` to the server's process group, falling back to the root's pid while the root is not
   yet reaped. When both fail, nothing is escalated.
3. Within `GracefulShutdownTimeout`, the root's exit, then the group's end: `kill(-group, 0)` until
   no member is left. A descendant that moved into a session of its own (the persistent-terminal
   daemon, PTY shells) is not a member and is left to the server, as upstream leaves it.
4. `SIGKILL` to the group (with the same pid fallback) when a member is left, then a bounded wait
   of 10 seconds for the root.
5. With a collector, the output drains for at most one second, and the collection closes.
6. Stdin closes, the readers are released, and the pipes close.

The pinned server answers the `SIGTERM` the way its own runtime answers an interrupt: it shuts down
and exits with code 130. A failed start — timeout, cancellation, an early exit, closed stdout, or a
non-contract first line — runs the same ladder with the configured grace, as upstream's scope close
does. A canceled or timed-out start can therefore take up to that grace plus 16 seconds to throw:
at most 10 for the root's exit after `SIGKILL`, 1 for draining the output, and 5 for releasing the
readers. Disposal is bounded the same way, its drain running only with a collector. The ladder
runs once per server, and releasing a server's handles runs it first when nothing has yet, so a
start that an unexpected failure ended still ends its server. A server whose readers or exit watch
cannot start is killed with its group at once and reaped by the failing start
itself, which asks `waitpid` without hanging for at most 10 seconds and then leaves the asking to a
background loop that holds no thread and ends with an unknown exit on an unexpected fault, because the
failed start already threw to its caller, no caller remains to receive the fault, and the system
reaps an orphaned child when the host exits; the watch, once it started, is the child's only reaper. Stdin is
the ownership lease on Linux and macOS too, and it is the only channel left when the owner dies
without disposing: the session the server leads takes the hangup and interrupt paths away, so the
server exits on the lease's end-of-stream.

The launcher's recorded divergences from upstream on Linux and macOS (ADR-0031, ADR-0032):

| Divergence | Upstream | Launcher | Reason |
|---|---|---|---|
| Forced-exit wait | Waits on the root's exit alone after `SIGKILL` | Bounds it at 10 seconds | Disposal never hangs on a process the kernel cannot end. |
| Signal end | Fails `exited` with `KilledBySignal`, carrying the signal's name | Completes `Exited` with the signal as a value: its name and its number | A task that never faults is the idiomatic form, and the number is a superset. |
| Stderr | Discards it | Drains it into a bounded tail for startup diagnostics, and honours `OPENCODE_PRINT_LOGS=1` by handing over the host's stderr | A superset of upstream's behaviour. |
| Signals to the owner | Closes the scope on `SIGINT` and `SIGTERM` | Installs no process-wide signal handler | A library does not take over its host's signal handling; the host ends the server through `DisposeAsync`. |
| Children at exit | A synchronous exit hook sends `SIGKILL` to the group of every detached child still running | Installs no process-exit hook | The hook covers a process's own detached children, so the server applies it to the tool processes it spawns. The SDK's only child, the server, ends through the job on Windows and the stdin lease elsewhere, and a managed exit hook would not run on a crash or a `SIGKILL`. |
| Waiting for the group | Waits for the root's own output pipes to close | Waits for the root's exit, then probes the group until it is empty | No dependency on who holds the pipes. Three consequences at the edges: a same-group member that ignores `SIGTERM` but holds none of the root's pipes is ended by `SIGKILL`, where upstream leaves it running; a detached descendant that holds the root's stdout does not delay the close beyond a collector's one-second drain, where upstream waits the full grace and then sends `SIGKILL` to the group; a root that exits with code 0 while its stdout is still open leaves the group untouched, where upstream sends `SIGTERM` to it within its one-second output deadline. |

The launcher's recorded divergences from upstream on Windows (ADR-0031, ADR-0032):

| Divergence | Upstream | Launcher | Reason |
|---|---|---|---|
| Already-exited root | Can run `taskkill` on the dead pid while it waits for the output deadline | Runs no tree kill | `taskkill` finds only live processes, so the run reaches none of the root's descendants and only costs time. |
| Waiting for the root | Waits for the root's own output pipes to close | Waits for the root's own exit | No dependency on who holds the pipes: a detached descendant that holds the root's stdout does not hold the close open. |
| Exit code | Bun truncates it to 8 bits | Reports the operating system's code in full, as a signed 32-bit value | The exact value. |
| Stderr | Discards it | Drains it into a bounded tail for startup diagnostics, and honours `OPENCODE_PRINT_LOGS=1` by handing over the host's stderr | A superset of upstream's behaviour. |
| Job assignment | Assigns the server right after creating it, the server already running | Creates the server suspended, assigns it, then resumes it | The server runs no instruction outside the job, and the launcher's job always nests under the host's chain, never under a job a libuv server creates for itself before the assignment, which would make every later assignment of the host fail with `ERROR_ACCESS_DENIED`. Residual: an owner that crashes between the creation and the assignment leaves that server suspended until logoff. |
| Job creation failure | Aborts the process | Fails the start with `OpenCodeServerException`, keeps no record of the failure, and the next start creates the job again | A library does not abort its host, and a transient failure must not fail every later start. |
| Inherited handles | Passes every inheritable handle; the child's ends are inheritable from their creation | Passes an explicit list; the child's ends are inheritable only around `CreateProcessW`, under one SDK lock | The server receives only its own standard handles, so a parent capturing the host's output sees it end. The handles the host inherited from its own ancestors, which upstream passes on, are not passed either. Residual: a concurrent `Process.Start` of the host can inherit a duplicate inside the call on every runtime, because the SDK's spawn does not take the runtime's private spawn lock. |
| Assignment refused with `ERROR_INVALID_PARAMETER` | Never meets it: libuv adds the calling process to its job first, the workaround its own comment describes; a refusal would kill the server and fail the spawn | Tries once more in a fresh job, which serves later starts only when it takes the server, keeping the old one held | libuv's comment records a job whose first assignment was a Windows Store program refusing every later one this way. The launcher does not add the host (ADR-0032), so it can meet that, and one retry keeps every later start working. |
| Tree kill | Runs `taskkill` through `cmd.exe` and `PATH`, and leaves a run that hangs | Runs it from System32 with no handle of the host's, and ends a run its bound overtakes | No substituted executable, and nothing outlives disposal. |
| Forced-exit wait | Waits on the root's exit alone after the second rung | Bounds it at 10 seconds | Disposal never hangs on a process the kernel cannot end. |
| Signals to the owner | Closes the scope on `SIGINT` and `SIGTERM` | Installs no process-wide handler | A library does not take over its host's signal handling; the host ends the server through `DisposeAsync`. |

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
failure that reaches a running child: an exit before readiness (naming the exit code — on Windows in
its full 32 bits, a negative status code with its hexadecimal form — or on Linux and macOS the
terminating signal), a stdout that closed before readiness, a readiness timeout (naming the
configured bound), and a non-contract first stdout line (quoting it) all carry it. The pre-spawn
failures carry none, because nothing ran: an unresolvable command (naming the command, the
directories searched, and the extensions tried), a refused batch argument (naming the argument and
the shim), an unsupported POSIX platform, a spawn or pipe-creation failure (wrapping the underlying
`Win32Exception` or `IOException`, naming the caller's spelling and, when they differ, the resolved
path, and the working directory when one was set), and on Windows a server that could not be placed
in the job or resumed (wrapping the `Win32Exception`; the server was ended before it ran). A NUL in
a command entry, an environment entry or the working directory is refused with `ArgumentException`
for `options` before the command is resolved, because the spawn would silently cut the text there.
A caller token canceled before the launch boundary prevents process creation. Caller cancellation
observed during the readiness wait is reported through the public cancellation contract after the
child is torn down; cancellation during teardown does not reclassify an earlier failure.

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
through `Environment` — observed on Windows as well as on Unix.

**Explicit endpoint** (CLI `--server` parity; upstream builds a plain client plus a 5-second-bounded
status check and a version warning, `server-connection.ts:24-39`) → plain `OpenCodeClient`
construction. There is no dedicated SDK verb or member for this door; the validation recipe
composes two existing pieces: construct the client against the known endpoint, call
`Server.GetInfoAsync` under a caller-owned `CancellationTokenSource(TimeSpan.FromSeconds(5))` — a
total deadline, as upstream's `AbortSignal.timeout(5_000)` is, where `NetworkTimeout` bounds only
the time between progress — and compare the returned `ServerInfo.Version` against the caller's own
expectation. The SDK carries no version comparand of its own — the accepted snapshot
(`spec/SNAPSHOT.md`) is a protocol identity, not a runtime version — so a dedicated helper would
need a comparand the SDK does not have. The sandbox's `StandaloneServerWalkthrough` demonstrates
the recipe, running the same tail without `StartAsync` once a caller already holds an endpoint.

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
`disabled` must be a boolean, `remote` an object holding a string `route` (or, in the legacy
shape, a boolean), and `remoteAccepted` the literal `true` when present — is no config at all, so its `env` overlay and the legacy migration
it gates do not apply; a legacy-shape document still reads, and only the current shape is migrated.
The CLI's read rewrites a legacy-shape document in place; the SDK, which never authors the config's
contents, reads it as it is and leaves the rewrite to the daemon's next read, which reads the same
`env`. Remote access is set only through the CLI (`opencode service set remote`): the SDK reads the
key and never writes it, and the service's remote URL reaches callers in `ServerInfo.Urls` while its
tunnel is attached. The config's `disabled` key is read only by CLI commands: the connection-mode
selector (`resolve` starts a standalone server instead of reaching the service when it is true) and
the pairing command's guard (`opencode pair` needs the service); the pinned client's
`Service.discover`, `ensure`, and `stop` do not read it, and neither do `DiscoverAsync`,
`EnsureAsync`, and `StopAsync` — a caller that honors a disabled service chooses `StartAsync`, as
`resolve` does.
The registration and service-config files are read the way libuv opens files, sharing read, write,
and delete, so a poll never makes the daemon's remove-on-exit fail on Windows, and a UTF-8
byte-order mark is skipped as the CLI's decoder skips it. A daemon bound to every interface
registers the unspecified address (`0.0.0.0` or `::`), which Bun connects to as loopback and .NET
refuses as a target: the connect target becomes the same family's loopback, while the registered
URL is kept verbatim for identity.
The info probe is a raw authenticated `GET /api/info` through an owned non-redirecting handler with a
two-second bound: it rides neither the pipeline's decoration policy nor its progress window, and
`ServiceInfoProbe` owns its transport and path, while `ServiceProbeResponseClassifier` decodes
only the required first-party pid/version evidence. Every owned loopback connect on Windows
(.NET 5 and later), in both the probe's transport (`LoopbackTransport`) and the pipeline's owned
handler, disables SYN retransmission (`SIO_TCP_INITIAL_RTO`, the option libuv, Go, and the pinned
client's own runtime set for loopback), so a refused port is reported at once instead of after the
retransmissions that would outlast the bound. The no-proxy rule stays the probe's own: a loopback
endpoint is never routed through a proxy, so an environment proxy without `NO_PROXY` cannot hide a
live daemon. On .NET Framework, the pipeline's owned handler keeps Windows' default SYN
retransmissions, so a refused loopback port reports through the connect bound's "did not answer"
text, not as a refusal: `HttpClientHandler` has no connect seam, and a refusal check before each
attempt would add a second mechanism to the hot path. A failed exchange is classified refusal
first. The caller's cancellation propagates before anything else. A refused connection anywhere in
the failure's chain of inner exceptions is no service, whatever the bound says: the runtime keeps
the refusal as the inner exception of the cancellation it rethrows when the bound expired
meanwhile. Only a refusal counts, not a failed name lookup or an unreachable network. Any other
failure counts as a timeout exactly when the bound expired, whatever its type (the pinned client's
`timedOut: signal.aborted`). A dead daemon therefore classifies as no service on every host once its
refusal reaches the runtime; only a probe whose bound expires before its connect is refused reports
a timeout. On `net472` and `netstandard2.0`, where the platform handler has no connect seam, the
probe learns the refusal over a raw pre-connect with the same option before it sends the request.
That connect takes no token, because the downlevel connect that takes one reports a cancellation in
place of a refusal that completes while the token is being cancelled. The bound closes its socket
instead, so a refusal stands, after the caller's cancellation as any failure does, and a connect
the bound cut short is the bound's timeout. This
private discovery decoder is independent of the generated public `ServerInfo` model, whose required `Urls` does not constrain discovery. Discovery returns null for a missing, unusable, or not-ready
registration and throws only for refused input (`ArgumentException`), caller cancellation, and an
unresolvable user home (`OpenCodeServerException`).

`OpenCodeServer.StopAsync` is the CLI's `service stop`. It resolves the registration the way
discovery does (channel rules, legacy migration, or a direct file) and reads it once; asks a ready
and compatible daemon to shut its persistent terminals down through the generated
`PersistentPtys.ShutdownAsync` door, ignoring a refused or failed answer the way the CLI does;
removes the handoff sidecar the CLI keeps beside the registration (`<file>.pty-handoff`); then ends
the registered process the way the pinned client's `terminate` does — `SIGTERM`, the pin's own poll
for the process to leave (every 50 ms, up to 100 times), `SIGKILL` when it is still there, the same
poll again. On Windows both rungs are a hard kill, which is what the pinned client's runtime does
with a `SIGTERM` there. The registration is re-read and its `id`, `version`, `url`, and `pid`
compared before the first signal and before the removal, so a record another service replaced is
never signalled or removed; between the rungs it is not read, because it can disappear or change
hands before the signalled process exits, so the kill rung follows that process alone, as the pinned
client's does. The process is identified as (pid, start time), compared immediately before every
send and at every look of the poll, so a pid the operating system reuses during the stop is never
signalled and reads as the registered process being gone (ADR-0026); a reuse before the stop runs is
not detected, because the registration records no start time. A daemon that is an unreaped child of
this process is waited for until it is reaped, so at return its pid names no process, as the pinned
client's `kill(pid, 0)` check requires; a zombie another parent leaks reads as gone.
A missing or corrupt registration completes successfully; a sidecar that cannot be removed is
ignored, as the pinned client's `stop` ignores it, so the process is still ended; a process still
running after the kill rung throws `OpenCodeServerException`, the registration then left in place;
cancellation before a signal prevents it and after one ends the wait. Stop targets exactly one pid,
never a tree, is never implied by disposal or host shutdown, and may end a service other clients
share.

`OpenCodeServer.EnsureAsync` is the CLI's managed-service election (`Service.ensure`). Its options
are validated and copied before the registration is read or anything is spawned; `Replace` and
`Error` need `ExpectedVersion`. A NUL in a command entry or an environment entry is refused during that
validation with `ArgumentException` for `options`, because a spawn would silently cut the entry. It
reuses a ready compatible daemon, replaces a version-mismatched one according to
`OpenCodeServerEnsureOptions.VersionPolicy` (Ignore strips the expected version, Replace keeps it in
the loop, Error discovers twice and throws on a ready mismatch without entering the loop), and
otherwise spawns at most two detached contenders (`opencode serve --service` by default) until a
service registers or the 120-second wall-clock bound expires. A daemon whose health protocol is
incompatible (an authenticated 404 on `/api/info`) fails the call while its version satisfies the
loop's requirement; only an unmet version requirement replaces it. A registration without a password
never wins. The first contender failure is held: while it stands no further contender is recruited,
the call throws it once no contender is left live, and the bound expires with it rather than with
the timeout. Three consecutive probe timeouts on one registered daemon end it through the stop
ladder — like Stop, Ensure may end a service other clients share — after which a contender starts at
once. The replacement stop acts on the registration it probed, so a record that changed since is
never touched; its handoff or sidecar clear is best-effort and never skips the terminate, its
failures do not end the election, and when no contender failure is held the last one becomes the
timeout's inner exception. After a recovery or a replacement stop, contenders that are the stopped
process or have finished are released and a held failure is dropped. The spawned command registers
where its own compiled channel and environment say, so `Channel`, `Command`, and `Environment` must
agree with the registration the call reads, or the call waits out the bound. Contenders are reaped
by the SDK as they exit. On Windows a contender starts the way libuv starts a Node child spawned
`detached` and `windowsHide` with no standard stream inherited: `DETACHED_PROCESS |
CREATE_NEW_PROCESS_GROUP | CREATE_NO_WINDOW` with `STARTF_USESHOWWINDOW` and `SW_HIDE`, so it has no
console, roots a process group of its own, and any window it opens starts hidden. A batch shim,
such as npm's `opencode.cmd`, is the exception: its `cmd.exe` starts with `CREATE_NO_WINDOW` and
not detached, because a detached `cmd.exe` has no console to hand the program it runs, which would
then take a new console with a visible window and standard streams of its own in place of the
stderr pipe; that program inherits a console with no window, shared with no other process. `OnStart` fires
at most once. The returned handle is the same non-owning
shape discovery returns. Persistent-terminal sidecar I/O is behind `IServicePtyHandoff`: `prepare`
requests the handoff ticket under the request bound through the SDK's own request pipeline and keeps
it raw, so it reaches the replacement exactly as the route answered it — the receiving daemon
decodes it with its own schema — and publishes it beside the registration through an owner-only
temporary file and a replace-on-success rename (on Windows the POSIX-semantics rename recorded
below). It reuses
a fresh matching sidecar and never fails the replacement, only the caller's cancellation propagates:
whenever the request fails or answers no usable ticket (no `handoff` member, a ticket missing a
member the pin knows, or an expired one), it first re-checks for a sidecar a concurrent caller
published, then shuts the daemon's terminals down, ignoring any failure, and publishes a null
sidecar with a 30-second expiry; a sidecar that cannot be published leaves the replacement without a
handoff. `environment` adopts an unexpired sidecar whose source still matches the current
registration (or whose registration is gone) as `OPENCODE_PTY_HANDOFF` and removes the variable
otherwise; `complete` clears only a sidecar a different source wrote, with no expiry check; and
`clear` removes it idempotently, the same seam Stop's sidecar clear routes through.

The background service's recorded divergences from upstream on Windows (ADR-0031):

| Divergence | Upstream | SDK | Reason |
|---|---|---|---|
| Sidecar publish | The pinned client's `publish` (`packages/client/src/pty-handoff.ts`) renames the temporary file over the sidecar with `node:fs` `rename`, which fails with `EPERM` while any handle holds the destination, whatever that handle shares | A POSIX-semantics rename: `SetFileInformationByHandle(FileRenameInfoEx)` with `FILE_RENAME_FLAG_REPLACE_IF_EXISTS` and `FILE_RENAME_FLAG_POSIX_SEMANTICS`, on a source opened with `DELETE` access. A system or volume that refuses it with `ERROR_INVALID_PARAMETER`, `ERROR_NOT_SUPPORTED`, or `ERROR_INVALID_FUNCTION` (Windows before 10 1607, some Windows Server 2016 installations, FAT, ReFS, some network shares; the codes Git's own POSIX-semantics rename falls back on) takes the platform replace instead: `MoveFileEx` with `MOVEFILE_REPLACE_EXISTING`, or `File.Replace` on the downlevel targets | The sidecar's readers include holders the SDK does not control, such as real-time antivirus and the indexer, and they open a fresh file with delete sharing, as the SDK's own reader does. The POSIX-semantics rename replaces the destination under them, and they keep reading the contents they opened, so the failure class is removed rather than retried. It is the rename Bun itself makes in its install path. A holder that opened the sidecar without delete sharing still fails the publish, and the replacement then starts without a handoff, as after any failed publish. |
| Case-only collision in a non-ASCII name | Bun's spawn groups spellings with JavaScript's `toUpperCase`, which applies full Unicode case mapping, so `straße` and `STRASSE` are one variable | Groups with `ToUpperInvariant`, so those two stay separate variables. 158 code points map differently, none of them ASCII: those with a multi-character uppercase (`ß`, `ŉ`, `ΐ`) or a mapping .NET doesn't have (`ı`, `ƛ`) | No ASCII name is affected. The base class library has no full case mapping, so matching it would mean carrying a case table in the SDK for names that environment variables rarely use. |

The divergence is reversed when upstream changes its publish to one that survives such holders, or
when the POSIX-semantics rename proves unsound on a volume the SDK supports.

The daemon side of the lifecycle is the spawned CLI's, not the SDK's: the incumbent check, the
exit-0 loser on a port another daemon holds, the `chdir` into the user home, and the registration
write all run inside `opencode serve --service` (`server-process.ts`), and the election reads a
loser's exit 0 the way upstream's does. The CLI's `reconnect` and `restart` have no door of their
own; a caller composes them from `StopAsync` and `EnsureAsync`.

Each background-service door chooses `TimeProvider.System` once at its composition root:
Discover supplies its probe; Stop shares it across the probe, handoff and terminator; Ensure
shares it across the election collaborators and spawned contenders. Internal collaborators require
that provider, including the handoff ticket's request pipeline. Request deadlines, election and
termination polls, and contender exit backoff run on it. Election deadline comparisons and spawn
stamps read UTC, and handoff expiry reads UTC epoch milliseconds; neither is a monotonic
elapsed-time policy. Each request deadline arms after probe headers, after ticket pipeline
construction, or at entry to the separate shutdown fallback, respectively.

`BannedSymbols.Clock.txt` bans the system clock in shipped src outside the five counted
composition roots: launcher, client pipeline, Discover, Stop and Ensure.

Where the SDK deliberately differs from the pinned chain, it differs here, each for a recorded
reason (ADR-0031):

- **Stricter readers than upstream's `JSON.parse`.** The registration and service-config readers
  refuse a repeated member. The registration reader also refuses a pid that is not a positive
  32-bit integer and a URL that is not absolute HTTP or HTTPS. A refused document reads as absent.
  Reason: the repository's fail-closed boundary, which treats malformed state as missing rather
  than acting on it.
- **No registration without a password.** Discover and Ensure never hand out such a registration,
  where upstream would connect without credentials. Reason: the SDK never yields an
  unauthenticated connection. The pinned daemon always writes a password, so the two only differ
  for a hand-made file.
- **No proxy for a loopback probe.** Reason: an environment proxy without `NO_PROXY` would
  otherwise hide a live daemon.
- **A refusal is no service even when the probe's bound expires with it.** Upstream reads every
  rejected exchange as `timedOut: signal.aborted`; its runtime settles a refused loopback connect
  within microseconds, before its abort timer fires, so there too a refusal reads as no service.
  The probe reads the refusal before the bound. Reason: the bound's timer runs on a thread of its
  own, so under load it could expire after the runtime saw the refusal and before the probe read the
  failure, a window only the SDK had, which counted a dead daemon toward the three-timeout recovery.
  The one case that still differs: where upstream's abort timer runs before its settlement, upstream
  reports a timeout and the SDK reports no service. Recorded risk: on Windows a full listen backlog
  answers a connect with a reset, so a live but overloaded daemon reads as no service, and Ensure
  starts a contender that loses the race to register and exits 0; upstream's runtime gives the same
  answer there. Reversal: upstream states that a refusal its bound overtakes counts as a timeout by
  design.

Everything else follows the chain at the pin.
