# 📑 Pagination

Date: 2026-10-24

Two operations in the API return more than fits in one response, and both use the same opaque
cursor envelope: **listing a session's messages** and **listing sessions**. Both can be paged by
hand or walked for you. Everything else returns its whole answer at once.

- [🧾 The cursor envelope](#-the-cursor-envelope)
- [🎛️ Shaping the request](#️-shaping-the-request)
- [🔁 Paging by hand](#-paging-by-hand)
- [♾️ Or let the SDK page for you](#️-or-let-the-sdk-page-for-you)
- [📄 The same walk, one page at a time](#-the-same-walk-one-page-at-a-time)
- [⚖️ Which one to use](#️-which-one-to-use)
- [⏪ Replaying a session](#-replaying-a-session)

## 🧾 The cursor envelope

| Response | Items | Cursor |
|---|---|---|
| `SessionMessageListResponse` | `Messages` (`IReadOnlyList<ISessionMessageInfo>`) | `Cursor` |
| `SessionListResponse` | `Sessions` (`IReadOnlyList<SessionInfo>`) | `Cursor` |

`ListCursor` has two members, both nullable strings:

```text
Next      the cursor for the following page, or null when there is no following page
Previous  the cursor for the preceding page, available on explicit page calls
```

**A missing `Next` is the one and only end signal.** Do not infer the end from an empty page — an
empty page that still carries a `Next` means "keep going". And do not read a cursor: it is opaque,
never to be decoded, compared, incremented, or deduplicated. Hand it back exactly as you got it.

Both responses are ordinary [response envelopes](errors-and-responses.md#-the-response-spine), so a
page carries `Status`, `IsError`, `Error`, and `RawBody` and accepts per-call `NoThrow`:

```csharp
var page = await sessions.ListSessionsAsync(
    new SessionListRequest { Limit = 25 },
    OpenCodeRequestOptions.NoThrow);

Console.WriteLine(page.IsError
    ? $"HTTP {page.Status}: {page.Error?.Tag ?? "<untyped>"}"
    : $"{page.Sessions.Count} sessions, next cursor {page.Cursor.Next ?? "<none>"}");
```

## 🎛️ Shaping the request

Three channels do the paging, and both request types carry all three:

| Member | Type | Meaning |
|---|---|---|
| `Limit` | `int?` | Page size. The server applies its default when unset and enforces its own range. |
| `Order` | `ListOrder?` | `Ascending` or `Descending`. **First page only.** |
| `Cursor` | `string?` | The opaque continuation from the previous page's `Cursor.Next`. |

`SessionMessageListRequest` and `SessionListRequest` both inherit all three from the shared abstract
`ListRequest`. A request may also carry filters of its own beside them: `SessionListRequest` adds
`Search`, `Project`, `Directory`, `Subpath`, `ParentId`, and `Archived`.

For message listings, `Limit` accepts 1–200 and defaults to 50. `Order` cannot be combined with a
cursor. The manual snippets below drop it on continuation; the SDK's automatic walks do that for you.

### Scoping a session listing

An unfiltered session listing spans every session in the profile's session store, across projects
and directories, including children, forks, and archived sessions. Upstream returns 50 by default,
ordered by most recently **updated** first, not most recently created. `Archived = true` lists only
archived sessions; `Archived = false` excludes them. Omit it to include both.

Scope a listing with `Directory`, or with `Project` optionally narrowed by `Subpath`. `Subpath`
applies only with `Project`; both match by equality. `Directory` matches the stored directory
exactly: no descendants, no case folding, and no trailing-slash trimming. Dot segments and symlinks
are not resolved. The only normalization is `\` to `/` on Windows. These matching rules come from
upstream's implementation at the pin.

> **🧭 Session-list continuations use the cursor's scope.** The server takes the filters and order
> from the cursor, not from the request; only `Limit` is read again, so keep sending it. To change
> filters or order, start a new walk without a cursor.

## 🔁 Paging by hand

Ask for a page, use it, and stop when `Next` is gone:

```csharp
var session = client.Sessions.GetSessionClient(sessionId);
var request = new SessionMessageListRequest { Limit = 50, Order = ListOrder.Ascending };

while (true)
{
    var page = await session.ListMessagesAsync(request);

    foreach (var message in page.Messages)
    {
        Console.WriteLine($"{message.GetType().Name} ({message.Type})");
    }

    if (page.Cursor.Next is not { } next)
    {
        break;
    }

    request = new SessionMessageListRequest { Limit = request.Limit, Cursor = next };
}
```

Sessions page identically — the only differences are the request type and the item collection:

```csharp
var request = new SessionListRequest { Limit = 25, Order = ListOrder.Descending };

while (true)
{
    var page = await client.Sessions.ListSessionsAsync(request);

    foreach (var session in page.Sessions)
    {
        Console.WriteLine($"{session.Id}  {session.Title}");
    }

    if (page.Cursor.Next is not { } next)
    {
        break;
    }

    request = new SessionListRequest { Limit = request.Limit, Cursor = next };
}
```

Manual paging is what you want when the walk itself is yours: per-page `NoThrow` handling,
`Cursor.Previous` to move backwards, or a cursor you stop at and resume from later.

## ♾️ Or let the SDK page for you

`SessionClient.EnumerateMessagesAsync` and `SessionsClient.EnumerateSessionsAsync` are the automatic
companions to `ListMessagesAsync` and `ListSessionsAsync`. Each yields the **items**, lazily,
following `Next` for you:

```csharp
var stream = session.EnumerateMessagesAsync(new SessionMessageListRequest
{
    Limit = 50,
    Order = ListOrder.Ascending,
});

await foreach (var message in stream.WithCancellation(cancellationToken))
{
    Console.WriteLine($"{message.GetType().Name} ({message.Type})");
}
```

Sessions enumerate the same way, filters and all:

```csharp
var stream = client.Sessions.EnumerateSessionsAsync(new SessionListRequest
{
    Limit = 25,
    Order = ListOrder.Descending,
    Search = "build",
});

await foreach (var session in stream.WithCancellation(cancellationToken))
{
    Console.WriteLine($"{session.Id}  {session.Title}");
}
```

What it does with your request is the manual loop above, with one thing done better: the first
request goes out unchanged, and **every continuation is your request again** — same `Limit`, same
`Search`, `Project`, `ParentId`, and every other filter — with `Order` dropped and the returned
cursor put in place. It stops when a page comes back without a `Next`.

Four things to know:

- **It is a pull sequence over ordinary HTTP calls**, not a stream. Nothing is held open between
  pages, and stopping early (a `break`, a `return`, a cancelled token) simply means the next page is
  never requested.
- **Per-call options reach every page.** Both traversal methods take `OpenCodeRequestOptions`
  before the cancellation token. `Location` applies to the first request and every continuation,
  and `NetworkTimeout` bounds progress separately for each page. `NoThrow` is refused at the call,
  before enumeration: an API error on page seven still throws `OpenCodeApiException` out of the
  `await foreach`.
- **Cancellation reaches every request** and is also checked between buffered items, so a token you
  cancel mid-page takes effect immediately rather than after the current page drains. The token you
  hand to `Enumerate*Async` and the one you hand to `WithCancellation` are both observed.
- **Each enumeration is its own walk.** Keeping the returned sequence in a variable and enumerating
  it twice sends every request twice: it is a recipe, not a buffer.

## 📄 The same walk, one page at a time

`Enumerate*Async` returns a `CursorSequence<TPage, TItem>`. Enumerating it gives you the items; its
`Pages` property gives you the same walk as **page envelopes** — the very `SessionMessageListResponse` or
`SessionListResponse` the one-page call would have handed you, `Status`, `Cursor`, `RawBody` and
all:

```csharp
await foreach (var page in session.EnumerateMessagesAsync(request)
    .Pages
    .WithCancellation(cancellationToken))
{
    Console.WriteLine($"{page.Messages.Count} messages, next {page.Cursor.Next ?? "<none>"}");
}
```

Reach for it when the cursor bookkeeping should be handled but the page is still part of what you
are doing — reporting progress per request, checkpointing `Cursor.Next`, or reading a page's status.
There is no page-size contract here: a page is exactly what the server returned for one request.

The two doors are independent walks of the same recipe, so enumerating the items and then the pages
performs both walks — two full sets of requests. The same per-call options apply to the `Pages`
walk. `NoThrow` is refused at the traversal call; an error page throws when the walk reaches it,
exactly as it does for items.

## ⚖️ Which one to use

| | `List*Async` | `Enumerate*Async` | `Enumerate*Async().Pages` |
|---|---|---|---|
| You get | one page envelope | the items, across all pages | every page envelope |
| Page metadata (`Status`, `Cursor`) | ✅ yes | ❌ no | ✅ yes |
| Per-call `NoThrow` | ✅ yes | ❌ refused at the call | ❌ refused at the call |
| Backwards paging via `Previous` | ✅ yes | ❌ no | ❌ no |
| Cursor bookkeeping | yours | handled | handled |

Reach for `Enumerate*Async` when you just want the messages or the sessions, and for its `Pages`
when you want the walk handled but the envelope kept. Reach for `List*Async` when you own the walk:
a paged UI, `Cursor.Previous`, or treating a failed page as data.

## ⏪ Replaying a session

Use the message walk above to select a `boundaryMessageId`, then choose a fork or an in-place revert:

1. **Fork without changing the source.** `Before` copies messages strictly before the named message;
   omit it to copy through the newest. Unfinished messages are skipped, and copies get new ids.

   ```csharp
   var fork = await session.ForkAsync(new SessionForkRequest { Before = boundaryMessageId }).ConfigureAwait(false);
   var replay = client.Sessions.GetSessionClient(fork.Fork.Id);
   ```

   A fork is not a child: its `ParentId` is null, and `Fork.SessionId` identifies the source.
   Deleting the source leaves its forks.

2. **Revert in place.** Stage restores files changed by assistant turns after the boundary, unless
   `Files = false`, and deletes no message:

   ```csharp
   await session.StageRevertAsync(new SessionRevertStageRequest { MessageId = boundaryMessageId }).ConfigureAwait(false);
   ```

   `ClearRevertAsync` undoes the stage. `CommitRevertAsync` deletes the boundary message and everything
   after it, keeping the staged file state. The next prompt or compaction commits a staged revert
   implicitly.

For a transcript instead, `client.Experimental.GetSessionExportAsync(sessionId)` returns the session
and every settled message in `SessionExport`, in ascending order, without paging.
