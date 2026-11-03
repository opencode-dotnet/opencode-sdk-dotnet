# QuickStart

Start its own private server, wait for model readiness, select a free model from the live catalog,
create a session, and stream its answer. The event bus opens before the prompt so no reply is lost.
On a first read, skip `FreeModel.cs`: it waits out the server's startup.

From any folder with nothing private in it, with the [playground prerequisites](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/playground/README.md#prerequisites):

```sh
dotnet run --project "<path-to-clone>/playground/QuickStart"
```

The sample uses a project reference. In your own app, install the SDK instead:

```sh
dotnet add package OpenCodeDotNet.Sdk --prerelease
```

The server uses your normal opencode data: this session lands in your opencode history.
Free models have limits, and the available list changes. The session runs in the folder you start
it from, so the agent loads that folder's instructions and config and can read its files, and what
it sends, your prompt included, goes to the model's provider. To use a configured provider instead, read the catalog and set
`SessionCreateRequest.Model` to a `ModelRef` with that model's `Id` and `ProviderId`.
See [Choosing a model](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/getting-started.md#choosing-a-model)
and [Streaming](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md).

This sample matches a turn's end by its typed event, so a server whose events have drifted from the SDK
can leave it waiting until its 5-minute deadline; [Waiting for a turn to end](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/docs/guide/streaming.md#-waiting-for-a-turn-to-end) shows the safer match.
It answers no permission asks, so a folder whose config makes the agent ask leaves it waiting the same way.
