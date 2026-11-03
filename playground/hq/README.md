# OpenCode HQ

HQ's first screen lists sessions with live status and a **Needs you** list. Permission asks have
Allow once and Reject buttons; forms are listed but cannot be answered yet. The session screen,
styling and the "How this screen works" panel come later. There is no login, database or settings UI.

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
shows and that server can run real prompts. This first screen shows sessions and answers asks, but
it cannot start a turn. Machine history runs nowhere, so Needs you stays empty and every status
stays idle until something drives HQ's own server; the session screen will.

**Know what is live:** that server runs its own sessions. Machine history shows as stored data.
Sessions run by other processes have no live status or streaming here, and their asks do not reach
Needs you. A `--service` boot during a turn on HQ's server, such as opening OpenCode Desktop,
resumes that session, so both would drive it. HQ cannot prevent this.

A session runs in its directory: the agent loads that folder's instructions and configuration and
can read its files. What it sends goes to the model's provider.

Listing permission requests or forms for a directory boots that location on the server, including
its plugins. So the board lists them only for directories of running sessions, and opening HQ does
not load your other projects.

## Architecture

```text
Aspire AppHost -> plain opencode serve (generated password, own port)
              -> HQ (endpoint + password) -> OpenCodeClient
                                         -> SessionBoard -> Blazor first screen
```

The board opens the bus, awaits its first event, seeds the newest **500 sessions**, reads which are
running, reads asks for those sessions' directories, then follows events. An unavailable directory
is logged and skipped.
A repeated `server.connected` refreshes the lists because disconnected events are lost. After a
failure surfaces, the screen says it is disconnected and the board retries. The SDK's own retries
can take about a minute before an exception surfaces; `Connected` may stay true during that time.
A stopped server stays stopped until you press Start on its resource in the dashboard.
`/health` separately probes the server under a short timeout instead of reporting a stale 200.

The dashboard shows HttpClient and ASP.NET Core traces and host logs, not SDK spans: the SDK's own
telemetry arrives in 1.1. The four OpenTelemetry packages export only when the AppHost supplies OTLP.
The projects use a ProjectReference here; a consumer uses `<PackageReference Include="OpenCodeDotNet.Sdk" />`.

| I want to learn | Folder | Reference |
|---|---|---|
| Explicit connection and health | `Features/Connection/`: `OpenCodeConnection.cs` and `OpenCodeHealthCheck.cs`, with `OpenCodeClient` and `Server.GetInfoAsync` | [Connection modes](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/connection-modes.md) |
| Pagination, live status, reconnects, permissions and forms | `Features/Sessions/`: `SessionBoard.cs`, with `Events.SubscribeAsync`, `Sessions.EnumerateSessionsAsync` and `Sessions.GetActiveAsync` | [Streaming](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md) |
| Typed not-found and other reply failures | `Features/Sessions/`: `SessionBoard.cs`, with `ReplyToPermissionAsync` | [Errors](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/errors-and-responses.md) |
