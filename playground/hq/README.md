# OpenCode HQ

HQ's first screen lists sessions with live status and a **Needs you** list. Permission asks have
Allow once and Reject buttons; question forms can be answered in place on both screens. HQ answers
text questions (option buttons and custom text) and multiselect questions (checkboxes); other field
kinds direct you to opencode. A session's page shows
its conversation, streamed answer, prompt box, asks and changed files; New session picks a directory.
There is no login, database or settings UI.

A session's page can open a **terminal** in the session's folder: one shell, its output as plain
text, and one line at a time. It leaves out resizing, reattaching and resuming from a cursor; the
SDK has all three ([Terminals](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/terminals.md)).
Leaving the page removes the terminal from the server, and closing the tab does so once the page's
connection times out. A terminal left by HQ being killed outright lives until the opencode server stops.

> **The terminal is a shell on the machine running opencode, for anyone who can open HQ's page.** HQ
> has no login and listens only on `127.0.0.1`, so keep it that way: run it on your own machine only.

Each screen has a hideable **How this screen works** panel: SDK calls for that screen, links to
their source files and a guide. It reads HQ's own source at run time, embedded at build time, so
snippets cannot drift from the code running. To add an entry, mark the code with a
`#region panel: name` block and its `#endregion`, then add a `PanelEntry` with that file and region
to the screen's list in `Features/Panel/PanelEntry.cs`.

## Run

Install the .NET SDK pinned by [global.json](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/global.json)
and put the opencode CLI on `PATH` as [Installation](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/README.md#-installation) describes.
No Docker, Aspire CLI or development certificate is needed.

```shell
dotnet run --project "<path-to-clone>/playground/hq/OpenCode.Playground.Hq.AppHost"
```

Open HQ from the dashboard, using the login link the AppHost prints to the console. If the opencode
resource shows Failed to start, the CLI isn't on `PATH`: see [Installation](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/README.md#-installation).
The AppHost starts its own plain `opencode serve` on the machine's normal opencode data, so history
shows and that server can run real prompts. Machine history runs nowhere, so Needs you stays empty
and every status stays idle until something drives HQ's own server, such as a session page's prompt.

**Know what is live:** that server runs its own sessions. Machine history shows as stored data.
Sessions run by other processes have no live status or streaming here, and their asks do not reach
Needs you. A `--service` boot during a turn on HQ's server, such as opening OpenCode Desktop,
resumes that session, so both would drive it. HQ cannot prevent this.

A session runs in its directory: the agent loads that folder's instructions and configuration and
can read its files. What it sends goes to the model's provider.

Opening HQ or a session page doesn't load an idle session's folder: the server loads a folder, with
its plugins, when a session in it runs, and the board lists asks only for those. Changed files show
the last turn's changes in a git repository, where opencode records them, and only for a session
running when its page opened or whose turn ended there.

## Architecture

```text
Aspire AppHost -> plain opencode serve (generated password, own port)
              -> HQ (endpoint + password) -> OpenCodeClient
                                         -> SessionBoard -> Blazor first screen
```

The board opens the bus, awaits its first event, seeds the newest **500 sessions**, reads which are
running, reads asks for those sessions' directories, then follows events. An unavailable directory
is logged and skipped.
A session renamed, deleted or archived elsewhere keeps its old row until the board reconnects or HQ restarts.
A repeated `server.connected` refreshes the lists because disconnected events are lost. After a
failure surfaces, the screen says it is disconnected and the board retries. The SDK's own retries
can take about a minute before an exception surfaces; `Connected` may stay true during that time.
A stopped server stays stopped until you press Start on its resource in the dashboard.
`/health` separately probes the server under a short timeout instead of reporting a stale 200.
Each open session page holds its own event stream, cancelled when the page closes.
Pages don't prerender: a prerendered button shows before its handler is attached, so an early click is dropped (#775). The page appears once its connection is up.

The dashboard shows HttpClient and ASP.NET Core traces and host logs, not SDK spans: the SDK's own
telemetry arrives in 1.1. The four OpenTelemetry packages export only when the AppHost supplies OTLP.
The projects use a ProjectReference here; a consumer uses `<PackageReference Include="OpenCodeDotNet.Sdk" />`.

| I want to learn | Folder | Reference |
|---|---|---|
| Explicit connection and health | `Features/Connection/`: `OpenCodeConnection.cs` and `OpenCodeHealthCheck.cs`, with `OpenCodeClient` and `Server.GetInfoAsync` | [Connection modes](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/connection-modes.md) |
| Pagination, live status, reconnects, permissions and forms | `Features/Sessions/`: `SessionBoard.cs`, with `Events.SubscribeAsync`, `Sessions.EnumerateSessionsAsync` and `Sessions.GetActiveAsync` | [Streaming](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md) |
| Typed not-found and other reply failures | `Features/Sessions/`: `SessionBoard.cs`, with `ReplyToPermissionAsync` | [Errors](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/errors-and-responses.md) |
| Stored conversations, streamed answers and prompts | `Features/Conversation/`: `SessionConversation.cs`, with `Events.SubscribeAsync`, `EnumerateMessagesAsync` and `PromptAsync` | [Streaming](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md) |
| Changed files and patches | `Features/Files/`: `ChangedFiles.razor`, with `GetDiffAsync` | [Requests](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/requests.md) |
| Answering text and choice questions | `Features/Forms/`: `FormDraft.cs` and `FormCard.razor`, with SDK `ReplyToFormAsync` via `SessionBoard` | [Requests](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/requests.md) |
| A terminal in a session's folder | `Features/Terminal/`: `SessionTerminal.cs`, with `CreatePtyAsync`, `ConnectAsync` and `RemoveAsync` | [Terminals](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/terminals.md) |
| Code behind each screen | `Features/Panel/`: `SourceSnippets.cs` and `PanelEntry.cs` read HQ's embedded regions | The panel links each entry to its source file and a guide |
