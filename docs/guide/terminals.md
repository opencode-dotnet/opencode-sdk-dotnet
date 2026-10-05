# 🖥️ Terminals

Date: 2026-09-12

opencode has two terminal families, and they are genuinely different animals:

| | **PTY** | **Persistent PTY** |
|---|---|---|
| Owned by | the server process | the `opencode-pty` daemon |
| Keyed by | its own id | a session id |
| Survives a server restart | no | yes, through a handoff |
| Output on the wire | text frames | raw bytes |
| Input | `SubmitAsync(string)`, or raw `WriteAsync(string)` | `SubmitAsync(string)`, or raw `WriteAsync(ReadOnlyMemory<byte>)` + `ResizeAsync` |
| Available on Windows | ✅ yes | ❌ no — [see the platform note](#-the-windows-platform-note) |

Both live sessions ride a **WebSocket**, not the HTTP pipeline. They are the only two doors in the
SDK that do, and they are hand-written for exactly that reason — a socket upgrade cannot be
generated from an OpenAPI document.

- [🧵 A normal PTY, end to end](#-a-normal-pty-end-to-end)
- [♻️ Resuming with a cursor](#️-resuming-with-a-cursor)
- [🧷 Persistent PTYs](#-persistent-ptys)
- [🛑 Cancellation, deadlines, and disposal](#-cancellation-deadlines-and-disposal)
- [🪟 The Windows platform note](#-the-windows-platform-note)
- [🎫 Connect tokens](#-connect-tokens)

## 🧵 A normal PTY, end to end

Create → take a handle → connect → read frames while you write input:

```csharp
var created = await client.Ptys.CreatePtyAsync(new PtyCreateRequest { Title = "sdk demo" });
var pty = client.Ptys.GetPtyClient(created.Pty.Id);

await using var terminal = await pty.ConnectAsync();

// SubmitAsync runs one command: it adds the carriage return a terminal's Enter key sends.
await terminal.SubmitAsync("echo hello");

long? cursor = null;

await foreach (var frame in terminal.ReadAsync(cancellationToken))
{
    switch (frame)
    {
        case PtyOutputFrame output:
            Console.Write(output.Text);
            break;
        case PtyCursorFrame control:
            cursor ??= control.Cursor;
            break;
    }
}
```

`PtySession` is small on purpose — `ReadAsync`, `SubmitAsync`, `WriteAsync`, `DisposeAsync` — and
it **owns its socket**, so disposing it is how you end the connection. `await using` does that for
you.

`SubmitAsync` takes **exactly one line**: no `\r` and no `\n` inside it — an embedded break would
submit a command you never wrote, so it is refused with `ArgumentException` — and an empty string
is a bare Enter. Nothing is trimmed and nothing else is changed.

`ReadAsync` yields `PtyFrame`, which has exactly two shapes:

- **`PtyOutputFrame`** — terminal output, already decoded to `Text`.
- **`PtyCursorFrame`** — one control frame, sent once when the replay ends, carrying the absolute
  output `Cursor` you have now caught up to.

Three rules worth knowing before your first surprise:

1. **Enter is `\r`.** That is the byte a terminal's Enter key sends, and `SubmitAsync` appends it
   for you — which is the whole reason to prefer it for running a command. `WriteAsync` is the raw
   door: partial input, control sequences, bytes that came out of a terminal emulator. It sends
   exactly what you give it and never rewrites a terminator, so a line ending in `\n` is a line
   feed rather than an Enter — a Unix line discipline may accept it, the Windows console host does
   not, and there the command renders and then sits at the prompt, unsubmitted.
2. **One read at a time.** A session carries one active read enumeration; starting a second
   concurrently throws `InvalidOperationException`. Writes are serialized for you. Canceling a read
   does not end the connection — see
   [cancellation, deadlines, and disposal](#-cancellation-deadlines-and-disposal).
3. **There is no end-of-command marker on this wire.** A terminal just goes quiet. If you need to
   know a command finished, either wait for the stream to settle or ask `GetAsync` for the
   PTY's status and exit code — the exit code is *not* on the socket.

Closing:

```csharp
var removed = await pty.RemoveAsync(null, OpenCodeRequestOptions.NoThrow);

Console.WriteLine($"removed: status={removed.Status} isError={removed.IsError}");
```

Removing a PTY while a read is in flight ends that read as a **normal close**, not as a fault. An
abnormal close, or a PTY that had already exited, throws `OpenCodeTransportException` naming the
reason.

## ♻️ Resuming with a cursor

A PTY outlives any connection to it, and the server retains its recent output. `PtyConnectOptions.Cursor`
picks what you get on attach:

| `Cursor` | You receive |
|---|---|
| omitted (`null`) | the whole retained buffer, replayed |
| `-1` | live output only, no replay |
| `0` or above | everything after that absolute output position |

```csharp
await using var resumed = await pty.ConnectAsync(new PtyConnectOptions { Cursor = cursor });
```

Feed it the `Cursor` from the previous connection's `PtyCursorFrame` and you get exactly what you
missed — no gap, no duplicate. An out-of-range value is refused client-side rather than silently
degrading into a full replay.

## 🧷 Persistent PTYs

These terminals belong to the `opencode-pty` daemon, are keyed to a **session**, and survive a
server restart. Creation is session-keyed; everything id-keyed sits on a handle:

```csharp
var created = await client.PersistentPtys.CreatePersistentPtyAsync(sessionId, new PersistentPtyCreateRequest
{
    Args = [],
    Env = new Dictionary<string, string>(StringComparer.Ordinal),
    Title = "sdk persistent demo",
}, OpenCodeRequestOptions.NoThrow);

if (created is { Status: 503, Error: ServiceUnavailableError { Service: "opencode-pty" } })
{
    Console.WriteLine("the opencode-pty daemon is not available on this host");
    return;
}

var terminal = client.PersistentPtys.GetPersistentPtyClient(created.PersistentPty.Id);
```

Then attach and drive it:

```csharp
await using var attached = await terminal.ConnectAsync(new PersistentPtyConnectOptions
{
    Role = PersistentPtyRole.Controller,
});

Console.WriteLine($"attached as {attached.Attachment.Role}, replay ends at {attached.Attachment.Replay.EndOffset}");

await attached.SubmitAsync("echo hello", cancellationToken);
await attached.ResizeAsync(cols: 100, rows: 30, cancellationToken);

await foreach (var frame in attached.ReadAsync(cancellationToken))
{
    switch (frame)
    {
        case PersistentPtyOutputFrame output:
            await sink.WriteAsync(output.Data, cancellationToken);
            break;
        case PersistentPtyResizedFrame resized:
            Console.WriteLine($"server resized to {resized.Cols}x{resized.Rows}");
            break;
        case PersistentPtyExitedFrame exited:
            Console.WriteLine($"exited with {exited.ExitCode?.ToString(CultureInfo.InvariantCulture) ?? "<none>"}");
            return;
        case PersistentPtyUnknownFrame unknown:
            Console.WriteLine($"unknown frame {unknown.Type}");
            break;
    }
}
```

What is different here:

- **`ConnectAsync` returns only after the server's attach**, so `Attachment` is always populated:
  the attachment id, the terminal as it stood, the replay bounds, the resize generation, and the
  **granted** role.
- **The role you asked for is not necessarily the role you got.** Ask for `Controller`, read
  `attached.Attachment.Role`, and believe the answer — input written from an observer connection is
  accepted by the SDK and dropped by the server. Set `Takeover = true` to take control from the
  current controller; without it a second controller is attached as an observer instead. Reusing a
  previous connection's `AttachmentId` is how a reconnect reclaims its own control.
- **Input has the same two doors as a normal PTY.** `SubmitAsync` submits one line — the same
  contract, the same `\r` — UTF-8 encoded into the framed input message that carries your viewport.
  `WriteAsync` is the raw door here too, and it takes bytes: partial input, control sequences, or
  whatever your emulator produced.
- **Output is bytes, never decoded.** `PersistentPtyOutputFrame.Data` is `ReadOnlyMemory<byte>`,
  because a frame is free to split a multi-byte character in half. Feed the bytes to a terminal
  emulator, or decode incrementally with a stateful `Decoder` — never with a fresh
  `Encoding.UTF8.GetString` per frame.
- **The frame family is richer**: attached, output, replay-complete, resized, exited,
  controller-changed, title-changed, foreground-process-changed — plus `PersistentPtyUnknownFrame`,
  which carries a control type this build does not know together with its raw JSON rather than
  failing your read. This socket is an experimental upstream surface and may grow frame kinds;
  that carrier is why a newer daemon will not break your loop.
- **Resuming uses offsets, not the `-1` trick.** `Cursor` is `null` or `0` for "from the oldest
  retained byte" — there is no live-only mode. To continue where you left off, anchor on the
  previous connection's replay-complete `EndOffset` or on the terminal's `Info.Output.Tail`. A
  cursor pointing at output that has been trimmed is advanced by the server, which reports the gap
  in the attachment's replay bounds.

The collection client also carries the daemon's lifecycle doors: `HandoffAsync` prepares the daemon
to outlive this server until a replacement claims it, and `ShutdownAsync` stops the daemon **and
every terminal it owns** — not just one.

## 🛑 Cancellation, deadlines, and disposal

Both terminal families share one lifetime model. This is what it means for a caller; the exact
contract is owned by [the client runtime architecture](../architecture/client-runtime.md).

**Canceling a read ends the read, not the connection.** A connection-owned receiver assembles
frames independently of your enumeration, so the token you hand `ReadAsync` bounds *your* wait and
nothing else. Stop reading, send input, start a new enumeration on the same session — it still
works:

```csharp
using (var window = new CancellationTokenSource(TimeSpan.FromSeconds(5)))
{
    try
    {
        await foreach (var frame in terminal.ReadAsync(window.Token))
        {
            // …stop once you have seen enough
        }
    }
    catch (OperationCanceledException)
    {
        // the wait ended; the connection did not
    }
}

await terminal.SubmitAsync("echo again");

await foreach (var frame in terminal.ReadAsync(cancellationToken))
{
    // same connection, still healthy
}
```

This is an SDK contract, not something `IAsyncEnumerable` gives you for free, and not an upstream
cancel/resume API. "One read at a time" still holds — it is a *concurrent* second enumeration that
throws, not a later one.

**Frames you have not read are queued, and the queue is not history.** The receiver holds only what
it has not handed you yet, so a connection nobody reads grows in memory; the queue is deliberately
unbounded until a real workload argues for a limit. It is not a replay buffer either — to go back,
reconnect with a [cursor](#️-resuming-with-a-cursor).

**When the connection ends, you still get what it already had.** A remote close or a transport
failure hands you the queued frames in order first, then completes normally or throws. That ending
is stable: a later read reports the same outcome rather than a fresh error.

**Disposal is the other path, and it does not wait for you.** `DisposeAsync` closes the socket,
abandons unread frames, and joins the connection's own work. Reads after it are empty, writes throw
`ObjectDisposedException`, and two callers disposing at once await the same cleanup.

**Sends carry their own deadline.** `PtyConnectOptions.SendTimeout` and
`PersistentPtyConnectOptions.SendTimeout` default to 30 seconds and bound each input or resize send
end to end, the wait for the send gate included:

```csharp
await using var bounded = await pty.ConnectAsync(new PtyConnectOptions
{
    SendTimeout = TimeSpan.FromSeconds(5),
});
```

It is a fixed total per call — progress does not restart it — and it is not a connect timeout, an
idle timeout, or a command-execution timeout. Expiry while the call is still queued fails that call
alone; expiry once the bytes are going out ends the attachment, because whether the server saw them
is no longer knowable. Nothing is retried. Your own cancellation stays
`OperationCanceledException`; the SDK's deadline is an `OpenCodeTransportException` naming a
timeout.

**A persistent PTY keeps your viewport intent.** The outbound size starts at the attachment's size
and moves when a `ResizeAsync` send succeeds. A `PersistentPtyResizedFrame` tells you how the
server is rendering — it does not overwrite what you asked for, and every input you send carries a
size coherent with its place in the send order.

## 🪟 The Windows platform note

**Persistent PTYs do not work on Windows hosts.** At the pinned snapshot, upstream's `opencode-pty`
daemon ships `darwin` and `linux` platform packages only — that is the whole root cause. The
server says so up front: `Server.GetInfoAsync()` reports `Capabilities.PersistentPty` as `false` on
a Windows host and `true` elsewhere; a server that predates the flag leaves `Capabilities` null.

`create` is the one route that starts the daemon, so on Windows it answers the API's declared
**HTTP 503** with a `ServiceUnavailableError` whose `Service` is `opencode-pty`. Every other route
takes its daemon-absent arm rather than erroring: `list` returns an empty list, `read` a null
payload, the id-keyed reads and writes a 404 they share with an unknown id, `shutdown` a 204, and
`connect` closes with 4404.

That is why the create snippet above uses `NoThrow` and checks for exactly that shape. Match the
**status and the service name together** — a bare 503 could be anything, and a 400 or 401 means
your request or credential was wrong, not that the daemon is missing.

Normal PTYs are unaffected and work on all supported platforms.

## 🎫 Connect tokens

Both families expose `CreateConnectTokenAsync`, which mints a short-lived single-use ticket for
handing a connection to a **browser**; the SDK's own `ConnectAsync` never uses one, because it
upgrades with the Basic credential it already holds — a header beats a single-use secret in a URL
that lands in logs.

```csharp
var token = await pty.CreateConnectTokenAsync();

Console.WriteLine($"ticket expires in {token.ConnectToken.ExpiresIn}");
```
