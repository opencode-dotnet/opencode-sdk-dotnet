# Launcher lives in the core package

Date: 2026-08-08

`OpenCode.Sdk` itself ships the `opencode serve` process launcher (start, readiness probe,
stop), hand-rolled with no process library. Upstream sets the
parity bar — `@opencode/client` ships `Service.ensure()`, which spawns `opencode serve --service`
from inside the client package — and the MCP C# SDK does the same (`StdioClientTransport` spawns
inside `ModelContextProtocol.Core`; kept as reference implementation). Evidence and lifecycle
anatomy: internal research, 2026-08-08, ".NET SDK design decisions: packaging, process management,
TFMs" and "Process management: CliWrap, raw Process, or the new .NET APIs?".

Where `System.Diagnostics.Process` cannot give upstream's result, the SDK binds the platform's own
primitives: `kill(2)` for the background service's Unix signal rungs (ADR-0026), `CreateProcessW`
and `posix_spawnp` for the detached contender (ADR-0027), and the same spawn, a Windows job, a
process-group signal, and its own exit status for the standalone launcher (ADR-0032). Each is a
function of the C library or the Windows process API every process already has loaded, not a
process library, so this record's rule holds.

## Considered options

- **Separate launcher package** — rejected: upstream parity argues for core, and the launcher
  is the SDK's own bootstrap path (a client without a server is useless in the local-first
  case).
- **CliWrap** — rejected for the product: the lifecycle is one known binary with known
  arguments; a dependency is not warranted. The rule is product-scoped — repo tooling may use
  CliWrap freely.
- **.NET 11 process APIs** (`ReadAllLinesAsync`, `KillOnParentExit`) — net11-only; planned as
  light-up, not baseline.
