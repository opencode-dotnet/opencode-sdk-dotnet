# 📡 Streaming

Date: 2026-10-15

Two server-sent event streams, both surfaced as `IAsyncEnumerable<T>` and both riding the same
transport as ordinary calls: the **global event bus** for everything happening in the server
process, and the **per-session log** for one conversation.

- [🔊 The global event bus](#-the-global-event-bus)
- [📜 A single session's log](#-a-single-sessions-log)
- [🧩 Unknown events do not break your consumer](#-unknown-events-do-not-break-your-consumer)
- [⏹️ Cancellation and lifetime](#️-cancellation-and-lifetime)
- [💥 Streams always throw](#-streams-always-throw)

## 🔊 The global event bus

`EventsClient.SubscribeAsync(CancellationToken)` yields `IEvent` — the union of every event the
pinned snapshot declares. Pattern-match the ones you care about and let the rest fall through:

```csharp
using var window = new CancellationTokenSource(TimeSpan.FromMinutes(5));

await foreach (var @event in client.Events.SubscribeAsync(window.Token))
{
    switch (@event)
    {
        case SessionExecutionSucceeded succeeded:
            Console.WriteLine($"session {succeeded.Data.SessionId} finished its turn");
            break;
        case UnknownEvent unknown:
            Console.WriteLine($"unknown event {unknown.Type}: {unknown.Payload.GetRawText()}");
            break;
        default:
            Console.WriteLine($"{@event.GetType().Name} ({@event.Type})");
            break;
    }
}
```

`IEvent` carries exactly one common member, `Type` — the wire discriminator. Every concrete event
type adds its own payload, so a `switch` on the type is the way in; `@event.Type` is there for
logging and for the frames you have not written a case for.

### 🧭 Routing by session

Every event that belongs to one session implements `ISessionScopedEvent`, whose `SessionId` names
that session, so you can keep the events of one session without listing their types:

```csharp
await foreach (var @event in client.Events.SubscribeAsync(window.Token))
{
    if (@event is ISessionScopedEvent { SessionId: var id } && id == sessionId)
    {
        Console.WriteLine($"{@event.Type} in {id}");
    }
}
```

The form events are not among them: a form's session id can be `"global"` rather than a session,
and `form.created` carries it inside the form. Read `Data.SessionId` on `FormCancelled` and
`FormReplied`, and `Data.Form.SessionId` on `FormCreated`.

### ⏳ Waiting for a turn to end

A session's turn ends with exactly one of three events: `SessionExecutionSucceeded`,
`SessionExecutionFailed`, or `SessionExecutionInterrupted`. The server publishes one each time the
session goes from working to idle, however many prompts it worked through. The samples here stop
on any `SessionExecutionInterrupted`, including one whose `Reason` is `Shutdown`. That interrupt means
the server is stopping, and the server resumes the turn after it restarts, with a new
`SessionExecutionStarted`. The bus [reconnects](#-a-live-bus-that-reconnects) by itself once the
server is back within a minute, so to follow the resumed turn keep reading and wait the same way.
Open the bus **before**
you prompt, because an event published before you listen is gone; the first event,
`EventServerConnected`, says the stream is live:

```csharp
var session = client.Sessions.GetSessionClient(sessionId);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
await using var events = client.Events.SubscribeAsync(deadline.Token).GetAsyncEnumerator();

// The first event is server.connected: from here on, nothing the session publishes is missed.
await events.MoveNextAsync();

await session.PromptAsync(new SessionPromptRequest { Text = "Summarize this repository." }, cancellationToken: deadline.Token);

while (await events.MoveNextAsync())
{
    if (events.Current is SessionExecutionSucceeded or SessionExecutionFailed or SessionExecutionInterrupted
        && events.Current is ISessionScopedEvent { SessionId: var id } && id == sessionId)
    {
        break;
    }
}
```

`SessionIdle` and `SessionStatusUpdated` are declared by the pinned document but not published by
the server it pins: upstream marks `session.idle` deprecated, and its session runtime reports turns
through the three execution events only. A loop that waits for either of them never ends.

### ✍️ Streaming an answer as it arrives

The answer's text arrives on the same bus, in pieces. This recipe does what upstream's own client
does:

```csharp
var session = client.Sessions.GetSessionClient(sessionId);
using var deadline = new CancellationTokenSource(TimeSpan.FromMinutes(5));
await using var events = client.Events.SubscribeAsync(deadline.Token).GetAsyncEnumerator();

// 1. The stream is ready once its first event, server.connected, has arrived.
if (!await events.MoveNextAsync() || events.Current is not EventServerConnected)
{
    throw new InvalidOperationException("The event stream did not start with server.connected.");
}

// 2. Prompt with an id of your own, so you can recognize this prompt's delivery.
var promptId = $"msg_{Guid.NewGuid():N}";
await session.PromptAsync(new SessionPromptRequest { Id = promptId, Text = "Summarize this repository." }, cancellationToken: deadline.Token);

// 3. Follow the session from the moment the prompt is delivered until its turn ends.
var delivered = false;
var texts = new Dictionary<(string AssistantMessageId, long Ordinal), string>();
var turnEnded = false;
while (!turnEnded && await events.MoveNextAsync())
{
    // Keep only this session's events; each of them names its session.
    if (events.Current is not ISessionScopedEvent { SessionId: var id } || id != sessionId)
    {
        continue;
    }

    switch (events.Current)
    {
        case SessionInboxDelivered { Data.InboxId: var inboxId } when inboxId == promptId:
            delivered = true;
            break;
        case SessionTextDelta { Data: var delta } when delivered:
            var part = (delta.AssistantMessageId, delta.Ordinal);
            texts[part] = texts.TryGetValue(part, out var sofar) ? sofar + delta.Delta : delta.Delta;
            Console.Write(delta.Delta);
            break;
        case SessionTextEnded { Data: var ended } when delivered:
            texts[(ended.AssistantMessageId, ended.Ordinal)] = ended.Text;
            break;
        case SessionExecutionSucceeded or SessionExecutionFailed or SessionExecutionInterrupted when delivered:
            turnEnded = true;
            break;
    }
}
```

- **Open the bus first, and wait for `EventServerConnected`.** The server sends it to every new
  subscriber before anything else, so once it has arrived, nothing the session publishes is missed.
  Upstream's client and the SDK both fail a connection that starts with anything else, and
  upstream's CLI waits for this event before it sends a prompt.
- **Filter by session once.** Every event that belongs to a session implements
  `ISessionScopedEvent`, so one check keeps this session's events and the `switch` below it needs
  no session test of its own.
- **Your own `Id` is how you find your prompt.** Upstream's CLI passes one too. The server requires
  only that it start with `msg_`; make it unique, as the `Guid` above does. When the session takes
  your prompt from its inbox, `SessionInboxDelivered` arrives with your id as its `InboxId`. Text the
  session streams before that moment belongs to an earlier turn.
- **`SessionTextDelta` carries the text; `SessionTextEnded` carries the final value.** Append each
  delta's `Delta` to its part, and when the part ends, replace what you have with `Text`. A part is
  identified by its `AssistantMessageId` and `Ordinal`, because one answer can hold several text
  parts. Deltas are live only and are never stored, whereas `SessionTextEnded` is durable and is
  what the session's messages keep.
- **Reasoning is separate.** `SessionReasoningDelta` and `SessionReasoningEnded` have the same shape
  for the model's reasoning; keep them out of the answer.
- **The turn ends with one of the three execution events.** They report the session going idle, not
  one prompt being answered: when several prompts are queued, one turn can work through all of
  them, so the event you stop on covers your prompt and anything delivered with it.

Messages already settled are on the session too: `EnumerateMessagesAsync` returns them, with each
text part in its final form.

**This bus is live and volatile, by the server's design, not the SDK's.** It has no filter, no
cursor, no replay, and no resume channel:

- Events published while you were disconnected are **gone**. There is no backfill to ask for.
- A consumer slower than the producer can overflow and fail the stream.

### 🔁 A live bus that reconnects

The SDK **reconnects** the bus the way upstream's own client does, and it tells you when it did:

- A connection that drops, fails, or goes 45 seconds without a byte is replaced one second later.
  The server's keepalive every 15 seconds keeps a quiet bus open, and the time your code spends on
  an event never counts as silence.
- Each connection has 2 seconds to answer and deliver `EventServerConnected`, or it counts as
  failed. A reachable server that is consistently slower than that fails every attempt, and the
  minute below applies.
- **Every connection starts with `EventServerConnected`.** A second one means you were
  disconnected and the events in between are gone: refresh the state you care about with ordinary
  calls and carry on reading.
- The stream never ends on its own. Your cancellation token ends it, disposing the client ends it
  at once with `ObjectDisposedException`, and so does one minute in which every attempt failed.
- That minute applies to the first subscribe too. **A wrong address or a refused password surfaces
  after one minute, not at once**, as the error the server last answered
  (`OpenCodeApiException`, `OpenCodeTransportException` or `OpenCodeStreamFailureException`).
  Upstream's client keeps retrying forever and shows a status; the SDK has no status to show, so it
  gives up instead of hanging your `await foreach`.

```csharp
var connectedBefore = false;
await foreach (var @event in client.Events.SubscribeAsync(cancellationToken))
{
    if (@event is EventServerConnected && connectedBefore)
    {
        // Reconnected: whatever happened in between is not on this stream.
        await RefreshStateAsync(cancellationToken);
    }

    connectedBefore |= @event is EventServerConnected;
    // ...
}
```

The per-session log below is the stream with a cursor, but it delivers events only from a server
started with event persistence — and the CLI you install does not start one that way; the note on
guarantees below has the whole story.

## 📜 A single session's log

`SessionClient.GetLogAsync(SessionLogRequest?, CancellationToken)` yields `ISessionLogItem` for one
session. Upstream serves it as an experimental route, so it needs an
[opt-in](experimental.md#opencode003) (`OPENCODE003`).

```csharp
var session = client.Sessions.GetSessionClient(sessionId);

await foreach (var item in session.GetLogAsync(new SessionLogRequest { Follow = QueryBoolean.True }, cancellationToken))
{
    switch (item)
    {
        case ISessionEventDurable durable:
            Console.WriteLine($"{durable.Type} seq={durable.Durable?.Seq}");
            break;
        case EventLogSynced marker:
            Console.WriteLine($"replay caught up at seq={marker.Seq}");
            break;
    }
}
```

Two arms cover the whole union, because the log is exactly *durable events plus one marker*. Every
durable event — all 43 of them — implements `ISessionEventDurable`, which carries the members they
all declare: `Id`, `Created`, `Metadata`, `Location`, and the `Durable` envelope with
`AggregateId`, `Seq`, and `Version`. The marker is not one of them: `log.synced` is a transition
boundary rather than something committed against the aggregate, so it sits beside the durable union
under `ISessionLogItem` and reports its watermark on its own `Seq`.

Those members are nullable on the interface for one reason: a `type` this build has never heard of
arrives as `UnknownSessionLogItem` or `UnknownSessionEventDurable`, which preserve the raw payload
and materialize no typed member. A concrete event's own `Durable` property is still non-nullable —
`SessionCreated.Durable` is a `SessionCreatedDurable` — so only the interface route needs the
`?.`.

`SessionLogRequest` has two members and both matter:

| Member | Type | Meaning |
|---|---|---|
| `Follow` | `QueryBoolean?` | `True` keeps the stream open and keeps delivering; otherwise the stream ends when the existing log has been sent. |
| `After` | `long?` | Continue after an aggregate sequence you have already processed — the explicit continuation channel the global bus does not have. |

The cursor is an exclusive aggregate sequence. Record the `Durable.Seq` of the last durable event
you processed — through the union interface, so the recipe works for every durable event rather
than one leaf type — and pass it back as it is:

```csharp
static SessionLogRequest? FollowAfter(ISessionEventDurable lastProcessed) =>
    lastProcessed.Durable is { } envelope
        ? new SessionLogRequest
        {
            After = envelope.Seq,
            Follow = QueryBoolean.True,
        }
        : null;
```

An unknown durable event has no envelope to read, so there is no sequence to resume from; keep the
last one you did read.

With `Follow = False`, a server that persists the requested history replays the available durable
events and ends with one `EventLogSynced`. The marker reports the captured aggregate watermark.
With `Follow = True`, read through any replayed events until that marker arrives; the same stream
then delivers live events committed after the attachment boundary, from a server that persists
them. The marker is a transition boundary, not a durable event to save as the next `After` value.

> **📎 A note on guarantees**: what the log delivers depends on how the server was started.
> Persistence is a server option that is off unless the process starting the server turned it on,
> and the log reads its events back from that persisted store — the live part of `Follow = True`
> included. A server without persistence answers a replay and a follow alike with the marker alone.
>
> **The distributed `opencode` CLI starts its server without event persistence and exposes no
> switch for it** — no serve flag, no environment variable, no configuration key. Observed on
> `@opencode/cli@2.0.15`. So `GetLogAsync` against a CLI-started server is not an error and not
> empty: with `Follow` unset or `False` it is one `EventLogSynced` and the end of the stream, and
> with `Follow = True` it is the same single marker and then nothing, however many durable changes
> the session goes through while you listen. The marker's `Seq` is the session's current aggregate
> sequence, which moves with every durable change although nothing is stored. That is the signature
> to look for — a marker, and no durable events ahead of it or after it. Persisted replay and
> follow need a host that embeds the opencode server library with persistence enabled; this
> repository's own simulation host does exactly that for its tests.
>
> For live session activity from a CLI-started server, subscribe to the
> [global event bus](#-the-global-event-bus) and filter by session id —
> `SessionRenamed.Data.SessionId`, for example. It is a different stream: the instance-wide
> `IEvent` union rather than one session's `ISessionLogItem`s, with no replay, no cursor, and no
> sync marker, and recovery after a disconnect is yours.
>
> When persistence *is* on, nothing expires or prunes the log and entries live until their session
> is deleted. Sequences are not contiguous, so treat a gap as ordinary rather than as loss. Carry a
> cursor only within one server's lifetime: a cursor past the log's tail is accepted rather than
> refused and then suppresses live delivery until the log overtakes it. See the canonical
> [server-sent events rules](../architecture/client-runtime.md#server-sent-events).

## 🧩 Unknown events do not break your consumer

The SDK is generated from a **pinned** snapshot of the opencode API, while the server you are
talking to may be newer. When a frame arrives with a `type` this build has never heard of, the
stream does not fail and does not skip it — it hands you `UnknownEvent`, carrying the raw `Type`
string and the untouched JSON body as a `JsonElement`:

```csharp
case UnknownEvent unknown:
    Console.WriteLine($"unknown event {unknown.Type}: {unknown.Payload.GetRawText()}");
    break;
```

The per-session log has the same escape hatch, `UnknownSessionLogItem`, with the same two members.
Practically: a server upgrade cannot break your event loop, you can log or even handle a new event
type before the SDK is regenerated, and when it is regenerated the frame simply arrives as its
typed self instead. This is also why the members a union interface promises are nullable: a carrier
answers `null` for every one of them and hands you `Payload` instead.

What this is *not* is a bucket for malformed data. A frame whose `type` is known but whose body
cannot be read is a protocol failure and throws — the carrier is for *unknown*, not for *broken*.

## ⏹️ Cancellation and lifetime

- **Streams open lazily.** Calling `SubscribeAsync` or `GetLogAsync` does not touch the network;
  the request goes out on the first `MoveNextAsync`, which is to say on the first `await foreach`.
- **The enumeration token is the off switch.** Cancelling it closes the stream and surfaces as
  `OperationCanceledException` in the usual place. A `CancellationTokenSource` with a timeout, as
  in the first snippet, is a perfectly good session budget.
- **`WithCancellation` works** where you did not pass a token at the call — `await foreach (var x
  in stream.WithCancellation(token))`.
- **Disposing the enumerator ends the connection**, which `await foreach` does for you on `break`,
  on `return`, and on an exception.
- On the event bus, a connection that receives nothing for 45 seconds is replaced, as above. The
  session log is bounded only while it opens: once open, a silent log stays open until you cancel,
  the server ends it, or it fails.

## 💥 Streams always throw

Streaming operations return a stream, not a response envelope — so they have **no `requestOptions`
parameter**, and [`NoThrow`](errors-and-responses.md#-ask-for-the-failure-as-data-instead) is not
available on them. There is no envelope to put an error on, so every failure is an exception:

```csharp
try
{
    await foreach (var @event in client.Events.SubscribeAsync(cancellationToken))
    {
        Console.WriteLine(@event.Type);
    }
}
catch (OpenCodeStreamFailureException failure)
{
    Console.WriteLine($"the server ended the stream: {failure.Causes.Count} declared cause(s)");
}
catch (OpenCodeTransportException transport)
{
    Console.WriteLine($"the stream broke: {transport.Message}");
}
```

- `OpenCodeStreamFailureException` means the **server** ended the stream with a declared failure
  frame; its `Causes` collection carries the typed causes and is never null.
- `OpenCodeTransportException` — its base class — covers everything else: a broken connection, a
  body cut mid-event, an undecodable frame, a stalled read. Catching the base handles both.
- `OperationCanceledException` stays itself. Your cancellation is never repackaged as a failure.

Ordering matters in that `catch` chain: `OpenCodeStreamFailureException` derives from
`OpenCodeTransportException`, so the specific one goes first.
