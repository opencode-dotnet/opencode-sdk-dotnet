# 🧪 Experimental APIs

Date: 2026-10-16

Upstream serves some of its API under `/api/experimental/`. The SDK exposes all of it, and marks
every member that reaches it with .NET's `[Experimental]` attribute, so the compiler tells you
before you depend on it.

Upstream may change or remove an experimental family in any release, and the SDK follows it: from
`1.0.0`, an experimental family may change in any minor version.

## Opting in

The first use of an experimental member is a compile **error** that names its diagnostic id:

```text
error OPENCODE001: 'OpenCode.Sdk.ExperimentalClient' is for evaluation purposes only and is subject to change or removal in future updates. Suppress this diagnostic to proceed.
```

Suppress the id where you accept it, around the call:

```csharp
#pragma warning disable OPENCODE001 // the experimental wait; upstream may change it
await client.Experimental.WaitForSessionAsync(sessionId, cancellationToken: deadline.Token);
#pragma warning restore OPENCODE001
```

or for the whole project:

```xml
<PropertyGroup>
  <NoWarn>$(NoWarn);OPENCODE001</NoWarn>
</PropertyGroup>
```

The diagnostic comes from the compiler, not the target framework: every target the packages ship,
`net472` included, reports it under the C# compiler of the .NET 8 SDK (Visual Studio 17.8) or
later. Older compilers ignore the attribute.

## OPENCODE001

The `ExperimentalClient` (`client.Experimental`) and the types only it uses: session stats, import
and export, skills, the session wait, instruction entries, text generation, well-known
integrations, adding, removing, connecting and disconnecting MCP servers, file write, the v1
migration status, and the config update.

## OPENCODE002

The persistent-PTY family (`client.PersistentPtys`): the collection and handle clients, the
WebSocket connect door with its session, options and frames, and the types only it uses. The
[terminals guide](terminals.md#-persistent-ptys) covers how it works.

## OPENCODE003

The Session family's experimental operations, today `SessionClient.ReadLogAsync`, the per-session
log stream, and the types only it uses. The [streaming guide](streaming.md#-a-single-sessions-log)
covers how it works.
