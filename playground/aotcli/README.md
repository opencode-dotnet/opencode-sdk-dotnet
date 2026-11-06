# AotCli

A Native AOT console tool that inspects a repository: its branch, its changed files, its top-level entries, and where
its opencode config comes from. It starts its own server in that folder and needs no model.

Publish it as a native executable, then run it on a folder, the current one by default. With the
[playground prerequisites](https://github.com/opencode-dotnet/opencode-sdk-dotnet/blob/master/playground/README.md#prerequisites):

```sh
dotnet publish "<path-to-clone>/playground/aotcli/OpenCode.Playground.AotCli" -c Release -r <rid> -o aotcli
./aotcli/OpenCode.Playground.AotCli <repository>
```

`<rid>` is your runtime identifier, such as `win-x64`, `linux-x64` or `osx-arm64`. To run
without the native build:

```sh
dotnet run --project "<path-to-clone>/playground/aotcli/OpenCode.Playground.AotCli" -- <repository>
```

The sample uses a project reference. In your own app, install the SDK instead:

```sh
dotnet add package OpenCodeDotNet.Sdk
```

The SDK declares `IsAotCompatible`. A measured Native AOT publish of this app reports no trim or AOT warnings.

Opening a folder runs it as a project: the server loads that folder's instructions, config and plugins. Inspect only
folders you trust. The server reads the branch in the background and announces it on the event bus, so the tool waits
up to five seconds for that announcement when the first read has no branch yet.
