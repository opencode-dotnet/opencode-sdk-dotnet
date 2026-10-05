# Background-service behaviour follows the accepted-pin first-party chain

Date: 2026-09-17

The registration file, its channel-dependent path, legacy migration, the info probe, and the
Ensure/Stop lifecycle are not in the OpenAPI document. The SDK ports the first-party CLI/Effect
chain at the accepted pin (`ServerConnection.resolve` -> `ServiceConfig.options` -> `Service` ->
`ServiceRegistration`/`ServerProcess`) and pins every consumed file in `spec/source-watch.json`;
that chain is the behaviour source. The public Promise helper's path handling and the Desktop
integration are comparison evidence only, because the Promise module resolves no channel and the
CLI is the only writer of the files the SDK reads. The Promise helper's `ensure` loop is the one
part the port follows statement for statement: at the pin it states the same election as the
Effect `Service.ensure` (spawn, probe, timeout recovery, replacement, contender harvest), and its
imperative form reads line for line in C#, so it is the template the Ensure port is written
against while the paths it reads still come from the CLI chain.
Where the SDK cannot mirror a compile-time fact (channel, installed version, self-spawn command)
it carries a named projection in the design and never a hidden default.

The probe is the pinned client's: an authenticated `GET /api/info` decoded for `pid` and
`version` alone, independently of the generated public info model and its required `urls`.
An authenticated 404 is read before any body and identifies the registered daemon as present
but incompatible — the pinned client's ensure fails on such a daemon, and replaces it only when a
version requirement is unmet — so discovery reports no service for it. The request bound and the owned non-redirecting transport are the SDK's own,
and the transport keeps the pinned client's classification on every host: a refused loopback
connect is no service, never a timeout, which on Windows takes the same no-SYN-retransmission
socket option the pinned client's runtime sets for loopback. One deliberate divergence is that the
probe never sends a loopback request through a proxy, where the pinned client would; the full list
of the SDK's deliberate divergences from the chain lives in `docs/architecture/client-runtime.md`.
ADR-0031 states the general rule this record applies to the background service: upstream as the
north star, and every divergence reasoned and recorded.

## Considered options

- Mirror the public Promise helper: simpler, but it hardcodes `<state>/opencode/service.json` and
  drifts from what the installed CLI actually does. Rejected.

Reversal: upstream moves registration and lifecycle into a package the client SDKs consume
directly, or replaces the file-based registration with another discovery contract.
