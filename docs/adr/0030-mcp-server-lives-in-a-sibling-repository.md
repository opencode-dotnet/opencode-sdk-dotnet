# The MCP server lives in a sibling repository

Date: 2026-09-29

The MCP server is developed in its own repository in the `opencode-dotnet` organization, not in
this one. It stays a thin adapter over the SDK, but it is a different product for a different
reader — the user of an MCP client rather than a .NET developer — with its own distribution (a
tool or a container rather than a library) and its own release rhythm. It consumes the SDK as a
published NuGet package, which makes it the SDK's first external consumer: it reaches the public
surface only, as any consumer does, where a project reference would let it reach internals and hide
gaps from the surface review before `1.0.0`. The organization exists to hold sibling projects like
it, and this repository's CI, gates, and canon stay scoped to the library.

This supersedes the repository placement ADR-0006 recorded; ADR-0006 keeps the versioning, and the
MCP server versions on its own in its repository.

## Considered Options

- **Same repository (ADR-0006).** Compile-time coupling surfaces an SDK breaking change in the same
  CI run, and the infrastructure is paid for once. Rejected: before the freeze the SDK surface still
  moves, and after it the coupling mostly re-tests what the published package already proves; the
  cost is a heavier gate on every SDK change and a consumer that never meets the package as users
  do.

## Consequences

- An SDK change reaches the MCP server when it takes a new SDK version, not in the same CI run.
  The MCP server codes against the SDK after the surface's namespace layout is decided, so the
  pre-1.0 surface changes do not land twice.
- The MCP server's consumed-operation set is derived from its own code, not inside one build.
- Reversal trigger: if the two change together routinely — paired pull requests across the
  repositories become the norm — co-location is reconsidered.
