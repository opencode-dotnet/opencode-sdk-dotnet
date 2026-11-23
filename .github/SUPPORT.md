# Support

## Getting help

### Questions about using the SDK

Read [the guide](../docs/guide/README.md) first. It covers installing, connecting, streaming,
terminals, errors, and pagination. If it does not answer your question:

Ask in the [Q&A category of Discussions](https://github.com/opencode-dotnet/opencode-sdk-dotnet/discussions/categories/q-a); usage questions are answered there.

### Bugs, missing operations and ideas

Blank issues are disabled. Use the form that fits:

- the [bug form](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/new?template=bug_report.yml),
  when the SDK behaves differently than documented;
- the [missing-operation form](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/new?template=operation_gap.yml),
  when an operation the server declares is absent, mis-shaped or mis-typed in the SDK;
- the [feature-idea form](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues/new?template=feature_idea.yml),
  for an idea that would make the SDK more useful.

Before you file a bug:

1. Search the [existing issues](https://github.com/opencode-dotnet/opencode-sdk-dotnet/issues).
2. Read the [Known Issues](../README.md#known-issues) section of the README.
3. Check which opencode server you ran. Each SDK release is built and tested against one opencode
   release, its pin. Check your release's section in the [CHANGELOG](../CHANGELOG.md), or open
   `spec/SNAPSHOT.md` at your SDK's release tag for the exact pin. If you test `master`, the
   current [`spec/SNAPSHOT.md`](../spec/SNAPSHOT.md) names it. A different `@opencode/cli` release
   is worth ruling out first.
4. Please try the latest release and tell us whether it still happens. When you can, try the
   nightly package too: the fix may already be on `master`.

The bug form asks for the package version and where the package came from, the opencode server
version, how the server was started, the target framework, the operating system, and a minimal
reproduction. Remove credentials and project content from everything you attach.

### The opencode server itself

This is an unofficial .NET client. It is not the opencode server. Wrong server behaviour, a route
that fails under any client, or a missing API belongs to
[opencode's issue tracker](https://github.com/anomalyco/opencode/issues), not here.

### Security issues

Never report a vulnerability in a public issue. Follow the private reporting instructions in
[SECURITY.md](SECURITY.md), which has its own response timeline.

## Support policy

**Package versions.** Only the latest release of `OpenCodeDotNet.Sdk` and
`OpenCodeDotNet.Sdk.Extensions` on NuGet.org is supported. Nightly packages from `master` get fixes
as they land.

**The opencode server.** Each SDK release is built and tested against one opencode release. Later
server releases usually work, but the tests do not run against them.

**.NET versions.** Support covers the runtimes the packages target: .NET 10, .NET 11, and .NET
Framework 4.7.2 or later. A project on another runtime, such as .NET 8, can install the
`netstandard2.0` asset, but that runtime is not tested or supported.
[Versioning and support](../docs/guide/versioning-and-support.md) has the rule.

**Experimental APIs.** An API that needs the `OPENCODE001` or `OPENCODE002` opt-in may change in
any release that moves to a newer opencode release.

**Response time.** Response time is not guaranteed.
