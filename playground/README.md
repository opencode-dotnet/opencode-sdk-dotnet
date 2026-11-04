# Playground

Runnable apps introduce the SDK through real workflows, with the SDK calls in view.
Each app has its own README explaining what it shows and how to run it.

## Choose your path

| I want to | App | Shape |
|---|---|---|
| Start in five minutes without an API key | [QuickStart](QuickStart/) | Console app |
| See live sessions and answer permission asks | [HQ](hq/) | Blazor Server with Aspire |
| Run the SDK in a hosted service | [Worker](Worker/) | Generic Host console app |

## Prerequisites

- The .NET SDK pinned by [global.json](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/global.json).
- The opencode CLI on `PATH`, installed as [Installation](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/README.md#-installation) says.

## SDK areas shown

QuickStart shows the server launcher, server info, model readiness and catalog selection,
session creation, prompting, text deltas, and session-scoped execution outcomes.
Worker shows a hosted event-bus subscription and an owned server's exit.
HQ shows paginated sessions, active status, event-bus reconnects, per-directory permission and form lists,
permission replies, typed errors, and server health.
The [guides](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/README.md)
remain the reference for the rest of the SDK.
