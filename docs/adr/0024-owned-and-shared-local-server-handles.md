# OpenCodeServer represents owned and shared local server modes

Date: 2026-09-16

`OpenCodeServer` is the one process-backed local-server handle. `StartAsync` returns an owned
standalone child whose disposal stops it; `DiscoverAsync` and `EnsureAsync` return a shared
registered service whose disposal is a no-op. Deliberate stop and restart follow the
[connection lifecycle](../architecture/client-runtime.md#connection-modes).
Ownership stays visible through `OwnsProcess`. A discovered handle has one operation
(`CreateClient`) and an empty disposal, the shape ADR-0019 rejects for generated API handles; that
ceremony is accepted here as the price of one type instead of a second near-identical public type
whose only difference would be disposal semantics. ADR-0019 governs generated family handles and
is not amended.

## Considered options

- A separate `OpenCodeService` handle: two types, two client-composition paths, and a caller who
  must know which one they hold before disposing. Rejected.
- Mode-dependent implicit Stop on dispose: a shared daemon other clients depend on would die with
  the first SDK handle. Rejected.

Reversal: a real consumer that cannot use one type without confusing ownership, or an upstream
lifecycle change that gives the two modes different endpoint or credential contracts.
