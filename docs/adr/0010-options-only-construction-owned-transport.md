# Options-only construction; the SDK owns a singleton-friendly transport

Date: 2026-08-15

Status: superseded in part by ADR-0040: the reversal trigger and the consumer-need condition for a
composition seam do not apply to an option upstream's first-party client declares.

`OpenCodeClient(OpenCodeClientOptions)` is the only public way to build a client. The SDK
owns its transport — `SocketsHttpHandler` with `PooledConnectionLifetime` on modern TFMs
(the BCL-documented long-lived-singleton pattern), `ServicePointManager` connection-lease
hardening on net472 (a GA gate under this posture) — and there is no public transport
injection: the `(HttpClient, options)` constructor is internal, IVT-visible to this
repository's tests and benchmarks only. `OpenCode.Sdk.Extensions` registers one singleton
client (sub-clients resolved from the same instance) without `IHttpClientFactory` or a
`Microsoft.Extensions.Http` dependency. Premise: a local-first daemon SDK ships to
production simple — transport extensibility is not built before a concrete consumer need,
and the asymmetry favors omission (re-adding a public constructor is additive; removing
one post-GA is a breaking major). This reverses the same-day seal that kept the caller-owned
constructor public, on a changed premise, not on evidence against it: the transport survey's
grounds for rejecting internalize+IVT (stock `AddHttpClient<TClient>()` support, the factory-path
guard) both dissolve once neither surface exists, and that seal's anonymous-mode and `BaseAddress`
guard machinery deletes with the doors it defended. Evidence: internal research, 2026-08-14,
"Caller-supplied transport: BYO-HttpClient precedent survey" and "How do client construction,
options, and DI align with .NET conventions?", with its two follow-ups of 2026-08-15.

## Consequences

- No consumer composition seam (proxy/TLS/resilience/telemetry handlers) is public today; the
  common proxy case rides the ambient `HttpClient.DefaultProxy`. Adding a seam requires a concrete
  consumer need and a deliberate design. Its absence is an accepted position, not an oversight.
- The mocking constructor is the consumer substitution point for testing.
- On downlevel targets, cancelling a live SSE enumeration disposes its response because the
  .NET Framework response stream cannot cancel a pending asynchronous read. The resulting disposal
  or I/O failure is classified through the caller token and remains caller cancellation rather than
  a transport failure.
- The factory-era DI lifetime hazards (#31) resolve by construction — singletons
  end-to-end, one pipeline, no transient-disposable tracking; a roster contract test
  keeps sub-client registrations complete as families grow.
- Reversal trigger: a concrete consumer demand that ambient mechanisms cannot meet — not
  ecosystem parity alone (the transport survey already documents that parity).
