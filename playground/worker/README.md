# Worker

A Generic Host with a hosted service that starts its own server through the launcher, reads the live event bus,
and logs each event. Stop it with Ctrl+C: the host stops with exit code 0, and the server it started ends with it.

From any folder with nothing private in it, with the [playground prerequisites](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/playground/README.md#prerequisites):

```sh
dotnet run --project "<path-to-clone>/playground/worker/OpenCode.Playground.Worker"
```

The sample uses a project reference. In your own app, install the SDK instead:

```sh
dotnet add package OpenCodeDotNet.Sdk
```

**When the server ends under it,** the worker logs how it ended and stops with exit code 1,
so a service manager set to restart on failure, such as systemd with `Restart=on-failure`, restarts it. That's what opencode's own clients do with a server they
started: they don't restart it themselves. A failed start or event subscription also logs the failure and exits with code 1.

The hosted service starts and owns its server, so no server exists when the container is built. When a server starts before the host, `AddOpenCode` takes its endpoint and password, as [Registering with dependency injection](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/connection-modes.md#-registering-with-dependency-injection) shows.

The server uses your normal opencode data, and the folder you start it from is its working folder, so it loads that
folder's instructions and config. The worker sends no prompts and needs no model. The bus reconnects by itself while the
server lives; see [A live bus that reconnects](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md#-a-live-bus-that-reconnects).
